use openmls::prelude::tls_codec::{Deserialize, Serialize};
use openmls::prelude::*;
use openmls_basic_credential::SignatureKeyPair;
use openmls_rust_crypto::OpenMlsRustCrypto;
use openmls_traits::{OpenMlsProvider, signatures::Signer};
use sha2::{Digest, Sha256};
use std::{
    collections::{HashMap, HashSet},
    panic::{AssertUnwindSafe, catch_unwind},
    sync::{
        Arc, Mutex, Once, OnceLock,
        atomic::{AtomicU64, Ordering},
    },
};
use zeroize::Zeroize;

#[cfg(hax)]
compile_error!("Formal-verification builds are not supported by the TCPTunnel prototype.");

const SUITE: Ciphersuite = Ciphersuite::MLS_128_DHKEMX25519_AES128GCM_SHA256_Ed25519;
const MAX_INPUT: usize = 8 * 1024 * 1024;
const MAX_HANDSHAKE: usize = 256 * 1024;
const MAX_ACTORS: usize = 128;
const MAX_MEMBERS: usize = 64;
const IDENTITY_PREFIX: &[u8] = b"TCPTunnel/IdentityKey/v1\0";
const PROOF_PREFIX: &[u8] = b"TCPTunnel/IdentityProof/v1\0";
type Result<T> = std::result::Result<T, i32>;
type Actors = HashMap<u64, Arc<Mutex<Actor>>>;
static ACTORS: OnceLock<Mutex<Actors>> = OnceLock::new();
static NEXT_ID: AtomicU64 = AtomicU64::new(1);
static PANIC_HOOK: Once = Once::new();

fn fingerprint(public_key: &[u8]) -> Vec<u8> {
    let mut hash = Sha256::new();
    hash.update(b"TCPTunnel/Identity/v1/Ed25519\0");
    hash.update(public_key);
    hash.finalize().to_vec()
}

fn check_credential(credential: &Credential, public_key: &[u8]) -> Result<()> {
    let basic = BasicCredential::try_from(credential.clone()).map_err(|_| 3)?;
    if public_key.len() != 32 || basic.identity() != fingerprint(public_key) {
        return Err(3);
    }
    Ok(())
}

fn check_members(group: &MlsGroup) -> Result<()> {
    if group.members().count() > MAX_MEMBERS || group.ciphersuite() != SUITE {
        return Err(5);
    }
    let mut identities = HashSet::new();
    for member in group.members() {
        check_credential(&member.credential, &member.signature_key)?;
        if !identities.insert(fingerprint(&member.signature_key)) {
            return Err(3);
        }
    }
    Ok(())
}

struct Actor {
    provider: OpenMlsRustCrypto,
    signer: SignatureKeyPair,
    credential: CredentialWithKey,
    group: Option<MlsGroup>,
    owner: Option<Vec<u8>>,
    is_owner: bool,
    package_created: bool,
    pending_commit: bool,
    output: Vec<u8>,
}

impl Actor {
    fn new() -> Result<Self> {
        let signer = SignatureKeyPair::new(SUITE.signature_algorithm()).map_err(|_| 4)?;
        Self::with_signer(signer)
    }

    fn with_signer(signer: SignatureKeyPair) -> Result<Self> {
        let provider = OpenMlsRustCrypto::default();
        if signer.signature_scheme() != SUITE.signature_algorithm() || signer.public().len() != 32 {
            return Err(3);
        }
        let challenge = b"TCPTunnel/IdentityKey/validate/v1";
        let signature = signer.sign(challenge).map_err(|_| 3)?;
        provider
            .crypto()
            .verify_signature(
                SUITE.signature_algorithm(),
                challenge,
                signer.public(),
                &signature,
            )
            .map_err(|_| 3)?;
        signer.store(provider.storage()).map_err(|_| 4)?;
        let credential = CredentialWithKey {
            credential: BasicCredential::new(fingerprint(signer.public())).into(),
            signature_key: signer.public().into(),
        };
        Ok(Self {
            provider,
            signer,
            credential,
            group: None,
            owner: None,
            is_owner: false,
            package_created: false,
            pending_commit: false,
            output: Vec::new(),
        })
    }

    fn clear_output(&mut self) {
        self.output.zeroize();
        self.output = Vec::new();
    }

    fn import_identity(input: &[u8]) -> Result<Self> {
        if input.len() > 256 || !input.starts_with(IDENTITY_PREFIX) {
            return Err(3);
        }
        let signer = SignatureKeyPair::tls_deserialize_exact(&input[IDENTITY_PREFIX.len()..])
            .map_err(|_| 3)?;
        Self::with_signer(signer)
    }

    fn run(&mut self, operation: u32, input: &[u8]) -> Result<Vec<u8>> {
        if input.len() > MAX_INPUT {
            return Err(5);
        }
        match operation {
            1 => {
                if !input.is_empty() {
                    return Err(1);
                }
                Ok(self.signer.public().to_vec())
            }
            2 => {
                if self.group.is_some() || self.package_created || !input.is_empty() {
                    return Err(2);
                }
                let package = KeyPackage::builder()
                    .build(SUITE, &self.provider, &self.signer, self.credential.clone())
                    .map_err(|_| 4)?;
                self.package_created = true;
                package
                    .key_package()
                    .tls_serialize_detached()
                    .map_err(|_| 4)
            }
            3 => {
                if self.group.is_some() || self.package_created || input.len() != 32 {
                    return Err(2);
                }
                let config = MlsGroupCreateConfig::builder()
                    .ciphersuite(SUITE)
                    .use_ratchet_tree_extension(true)
                    .max_past_epochs(0)
                    .sender_ratchet_configuration(SenderRatchetConfiguration::new(8, 128))
                    .build();
                let group = MlsGroup::new_with_group_id(
                    &self.provider,
                    &self.signer,
                    &config,
                    GroupId::from_slice(input),
                    self.credential.clone(),
                )
                .map_err(|_| 4)?;
                self.owner = Some(fingerprint(self.signer.public()));
                self.is_owner = true;
                self.group = Some(group);
                Ok(Vec::new())
            }
            4 => {
                if !self.is_owner || input.len() > MAX_HANDSHAKE {
                    return Err(2);
                }
                let package = KeyPackageIn::tls_deserialize_exact(input)
                    .map_err(|_| 3)?
                    .validate(self.provider.crypto(), ProtocolVersion::Mls10)
                    .map_err(|_| 3)?;
                if package.ciphersuite() != SUITE {
                    return Err(3);
                }
                check_credential(
                    package.leaf_node().credential(),
                    package.leaf_node().signature_key().as_slice(),
                )?;
                let group = self.group.as_mut().ok_or(2)?;
                if group.members().count() >= MAX_MEMBERS {
                    return Err(5);
                }
                let identity = fingerprint(package.leaf_node().signature_key().as_slice());
                if group
                    .members()
                    .any(|member| fingerprint(&member.signature_key) == identity)
                {
                    return Err(3);
                }
                let (commit, welcome, _) = group
                    .add_members(&self.provider, &self.signer, &[package])
                    .map_err(|_| 4)?;
                self.pending_commit = true;
                let commit = commit.tls_serialize_detached().map_err(|_| 4)?;
                let welcome = welcome.tls_serialize_detached().map_err(|_| 4)?;
                let mut output = Vec::new();
                output.extend_from_slice(&(commit.len() as u32).to_be_bytes());
                output.extend_from_slice(&commit);
                output.extend_from_slice(&(welcome.len() as u32).to_be_bytes());
                output.extend_from_slice(&welcome);
                Ok(output)
            }
            5 => {
                if self.group.is_some() || !self.package_created || input.len() > MAX_HANDSHAKE {
                    return Err(2);
                }
                let message = MlsMessageIn::tls_deserialize_exact(input).map_err(|_| 3)?;
                let welcome = match message.extract() {
                    MlsMessageBodyIn::Welcome(welcome) => welcome,
                    _ => return Err(3),
                };
                let config = MlsGroupJoinConfig::builder()
                    .max_past_epochs(0)
                    .sender_ratchet_configuration(SenderRatchetConfiguration::new(8, 128))
                    .build();
                let staged =
                    StagedWelcome::new_from_welcome(&self.provider, &config, welcome, None)
                        .map_err(|_| 3)?;
                let group = staged.into_group(&self.provider).map_err(|_| 3)?;
                check_members(&group)?;
                let owner = group
                    .members()
                    .find(|member| member.index == LeafNodeIndex::new(0))
                    .ok_or(3)?;
                self.owner = Some(fingerprint(&owner.signature_key));
                self.group = Some(group);
                Ok(Vec::new())
            }
            6 => {
                if !input.is_empty() || !self.pending_commit {
                    return Err(2);
                }
                self.group
                    .as_mut()
                    .ok_or(2)?
                    .merge_pending_commit(&self.provider)
                    .map_err(|_| 3)?;
                self.pending_commit = false;
                check_members(self.group.as_ref().ok_or(2)?)?;
                Ok(Vec::new())
            }
            7 => {
                // WARNING: Sending while a membership commit is pending can encrypt for the wrong member set.
                if self.pending_commit {
                    return Err(2);
                }
                if input.is_empty() {
                    return Err(1);
                }
                if input.len() > MAX_INPUT - MAX_HANDSHAKE {
                    return Err(5);
                }
                self.group
                    .as_mut()
                    .ok_or(2)?
                    .create_message(&self.provider, &self.signer, input)
                    .map_err(|_| 3)?
                    .tls_serialize_detached()
                    .map_err(|_| 4)
            }
            8 => {
                if self.pending_commit {
                    return Err(2);
                }
                let message = MlsMessageIn::tls_deserialize_exact(input)
                    .map_err(|_| 3)?
                    .try_into_protocol_message()
                    .map_err(|_| 3)?;
                let group = self.group.as_mut().ok_or(2)?;
                let processed = group
                    .process_message(&self.provider, message)
                    .map_err(|_| 3)?;
                let credential =
                    BasicCredential::try_from(processed.credential().clone()).map_err(|_| 3)?;
                let sender = credential.identity().to_vec();
                match processed.into_content() {
                    ProcessedMessageContent::ApplicationMessage(application) => {
                        if sender.len() != 32 {
                            return Err(3);
                        }
                        let mut output = vec![1];
                        output.extend_from_slice(&sender);
                        output.extend_from_slice(&application.into_bytes());
                        Ok(output)
                    }
                    ProcessedMessageContent::StagedCommitMessage(commit) => {
                        if Some(&sender) != self.owner.as_ref() {
                            return Err(3);
                        }
                        group
                            .merge_staged_commit(&self.provider, *commit)
                            .map_err(|_| 3)?;
                        if group.is_active() {
                            check_members(group)?;
                            Ok(vec![2])
                        } else {
                            Ok(vec![3])
                        }
                    }
                    _ => Err(3),
                }
            }
            9 => {
                if !self.is_owner || input.len() != 32 || Some(input) == self.owner.as_deref() {
                    return Err(2);
                }
                let group = self.group.as_mut().ok_or(2)?;
                let index = group
                    .members()
                    .find(|member| fingerprint(&member.signature_key) == input)
                    .ok_or(3)?
                    .index;
                let (commit, _, _) = group
                    .remove_members(&self.provider, &self.signer, &[index])
                    .map_err(|_| 3)?;
                self.pending_commit = true;
                commit.tls_serialize_detached().map_err(|_| 4)
            }
            10 => {
                if self.group.is_some() || self.package_created || !input.is_empty() {
                    return Err(2);
                }
                let mut key = self.signer.tls_serialize_detached().map_err(|_| 4)?;
                let mut output = IDENTITY_PREFIX.to_vec();
                output.extend_from_slice(&key);
                key.zeroize();
                Ok(output)
            }
            11 => {
                if input.is_empty() || input.len() > 256 {
                    return Err(1);
                }
                let mut message = PROOF_PREFIX.to_vec();
                message.extend_from_slice(input);
                self.signer.sign(&message).map_err(|_| 4)
            }
            12 => {
                if input.len() <= 96 || input.len() > 352 {
                    return Err(1);
                }
                let mut message = PROOF_PREFIX.to_vec();
                message.extend_from_slice(&input[96..]);
                self.provider
                    .crypto()
                    .verify_signature(
                        SUITE.signature_algorithm(),
                        &message,
                        &input[..32],
                        &input[32..96],
                    )
                    .map_err(|_| 3)?;
                Ok(Vec::new())
            }
            13 => {
                if input.len() > MAX_HANDSHAKE {
                    return Err(1);
                }
                let package = KeyPackageIn::tls_deserialize_exact(input)
                    .map_err(|_| 3)?
                    .validate(self.provider.crypto(), ProtocolVersion::Mls10)
                    .map_err(|_| 3)?;
                if package.ciphersuite() != SUITE {
                    return Err(3);
                }
                check_credential(
                    package.leaf_node().credential(),
                    package.leaf_node().signature_key().as_slice(),
                )?;
                Ok(package.leaf_node().signature_key().as_slice().to_vec())
            }
            14 => {
                if !input.is_empty() || self.pending_commit {
                    return Err(2);
                }
                let group = self.group.as_ref().ok_or(2)?;
                let mut output = group.group_id().as_slice().to_vec();
                output.extend_from_slice(self.owner.as_ref().ok_or(2)?);
                for member in group.members() {
                    output.extend_from_slice(&fingerprint(&member.signature_key));
                }
                Ok(output)
            }
            _ => Err(1),
        }
    }
}

impl Drop for Actor {
    fn drop(&mut self) {
        self.clear_output();
    }
}

fn actor(handle: u64) -> Result<Arc<Mutex<Actor>>> {
    ACTORS
        .get_or_init(|| Mutex::new(HashMap::new()))
        .lock()
        .map_err(|_| 6)?
        .get(&handle)
        .cloned()
        .ok_or(2)
}

fn guard(work: impl FnOnce() -> Result<()>) -> i32 {
    match catch_unwind(AssertUnwindSafe(work)) {
        Ok(Ok(())) => 0,
        Ok(Err(code)) => code,
        Err(_) => 6,
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn tc_mls_abi_version() -> u32 {
    5
}

fn register_actor(actor: Actor) -> Result<u64> {
    let mut actors = ACTORS
        .get_or_init(|| Mutex::new(HashMap::new()))
        .lock()
        .map_err(|_| 6)?;
    if actors.len() >= MAX_ACTORS {
        return Err(5);
    }
    let id = NEXT_ID.fetch_add(1, Ordering::Relaxed);
    if id == 0 {
        return Err(6);
    }
    actors.insert(id, Arc::new(Mutex::new(actor)));
    Ok(id)
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tc_mls_create(handle: *mut u64) -> i32 {
    guard(|| {
        PANIC_HOOK.call_once(|| std::panic::set_hook(Box::new(|_| {})));
        if handle.is_null() {
            return Err(1);
        }
        unsafe {
            *handle = 0;
        }
        let id = register_actor(Actor::new()?)?;
        unsafe {
            *handle = id;
        }
        Ok(())
    })
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tc_mls_import_identity(
    input: *const u8,
    length: usize,
    handle: *mut u64,
) -> i32 {
    guard(|| {
        PANIC_HOOK.call_once(|| std::panic::set_hook(Box::new(|_| {})));
        if handle.is_null() || input.is_null() || length == 0 || length > 256 {
            return Err(1);
        }
        unsafe {
            *handle = 0;
        }
        let input = unsafe { std::slice::from_raw_parts(input, length) };
        let id = register_actor(Actor::import_identity(input)?)?;
        unsafe {
            *handle = id;
        }
        Ok(())
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn tc_mls_destroy(handle: u64) -> i32 {
    guard(|| {
        ACTORS
            .get_or_init(|| Mutex::new(HashMap::new()))
            .lock()
            .map_err(|_| 6)?
            .remove(&handle)
            .ok_or(2)?;
        Ok(())
    })
}

#[unsafe(no_mangle)]
// WARNING: The caller must supply pinned, valid buffers; catch_unwind cannot recover from invalid pointers.
pub unsafe extern "C" fn tc_mls_execute(
    handle: u64,
    operation: u32,
    input: *const u8,
    length: usize,
) -> i32 {
    guard(|| {
        if length > MAX_INPUT || (length != 0 && input.is_null()) {
            return Err(1);
        }
        let input = if length == 0 {
            &[]
        } else {
            unsafe { std::slice::from_raw_parts(input, length) }
        };
        let actor = actor(handle)?;
        let mut actor = actor.lock().map_err(|_| 6)?;
        actor.clear_output();
        let output = actor.run(operation, input)?;
        if output.len() > MAX_INPUT + MAX_HANDSHAKE {
            return Err(5);
        }
        actor.output = output;
        Ok(())
    })
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tc_mls_output_size(handle: u64, length: *mut usize) -> i32 {
    guard(|| {
        if length.is_null() {
            return Err(1);
        }
        let actor = actor(handle)?;
        let actor = actor.lock().map_err(|_| 6)?;
        unsafe {
            *length = actor.output.len();
        }
        Ok(())
    })
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn tc_mls_copy_output(handle: u64, output: *mut u8, capacity: usize) -> i32 {
    guard(|| {
        let actor = actor(handle)?;
        let mut actor = actor.lock().map_err(|_| 6)?;
        if capacity < actor.output.len() || (!actor.output.is_empty() && output.is_null()) {
            return Err(1);
        }
        if !actor.output.is_empty() {
            unsafe {
                std::ptr::copy_nonoverlapping(actor.output.as_ptr(), output, actor.output.len());
            }
        }
        actor.clear_output();
        Ok(())
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn split_change(output: &[u8]) -> (Vec<u8>, Vec<u8>) {
        let length = u32::from_be_bytes(output[..4].try_into().unwrap()) as usize;
        let commit = output[4..4 + length].to_vec();
        let welcome_start = 8 + length;
        (commit, output[welcome_start..].to_vec())
    }
    fn pair() -> (Actor, Actor) {
        let mut alice = Actor::new().unwrap();
        let mut bob = Actor::new().unwrap();
        alice.run(3, &[7; 32]).unwrap();
        let package = bob.run(2, &[]).unwrap();
        let (_, welcome) = split_change(&alice.run(4, &package).unwrap());
        alice.run(6, &[]).unwrap();
        bob.run(5, &welcome).unwrap();
        (alice, bob)
    }

    #[test]
    fn restored_identity_is_stable_and_invalid_keys_are_rejected() {
        let mut original = Actor::new().unwrap();
        let mut identity = original.run(10, &[]).unwrap();
        let mut restored = Actor::import_identity(&identity).unwrap();
        assert_eq!(original.signer.public(), restored.signer.public());
        restored.run(3, &[11; 32]).unwrap();
        assert!(restored.run(10, &[]).is_err());
        let mut modified = identity.clone();
        let offset = modified.len() - 5;
        modified[offset] ^= 1;
        assert!(Actor::import_identity(&modified).is_err());
        assert!(Actor::import_identity(&identity[..identity.len() - 1]).is_err());
        modified.zeroize();
        identity.zeroize();
    }

    #[test]
    fn authenticated_roundtrip_and_replay_rejection() {
        let (mut alice, mut bob) = pair();
        let plaintext = "Привет, encrypted chat 🐍".as_bytes();
        let encrypted = alice.run(7, plaintext).unwrap();
        assert!(
            !encrypted
                .windows(plaintext.len())
                .any(|window| window == plaintext)
        );
        let result = bob.run(8, &encrypted).unwrap();
        assert_eq!(result[0], 1);
        assert_eq!(&result[1..33], fingerprint(alice.signer.public()));
        assert_eq!(&result[33..], plaintext);
        assert!(bob.run(8, &encrypted).is_err());
    }

    #[test]
    fn tampering_is_rejected_and_next_message_still_works() {
        let (mut alice, mut bob) = pair();
        let mut encrypted = alice.run(7, b"tamper target").unwrap();
        let end = encrypted.len() - 1;
        encrypted[end] ^= 1;
        assert!(bob.run(8, &encrypted).is_err());
        let valid = alice.run(7, b"after rejection").unwrap();
        assert_eq!(&bob.run(8, &valid).unwrap()[33..], b"after rejection");
    }

    #[test]
    fn newcomer_cannot_read_old_epoch_and_removed_member_cannot_read_new_epoch() {
        let (mut alice, mut bob) = pair();
        let old = alice.run(7, b"before Charlie").unwrap();
        let mut charlie = Actor::new().unwrap();
        let package = charlie.run(2, &[]).unwrap();
        let (commit, welcome) = split_change(&alice.run(4, &package).unwrap());
        assert!(alice.run(7, b"pending commit").is_err());
        alice.run(6, &[]).unwrap();
        assert_eq!(bob.run(8, &commit).unwrap(), vec![2]);
        charlie.run(5, &welcome).unwrap();
        assert!(charlie.run(8, &old).is_err());
        let bob_identity = fingerprint(bob.signer.public());
        let removal = alice.run(9, &bob_identity).unwrap();
        alice.run(6, &[]).unwrap();
        assert_eq!(charlie.run(8, &removal).unwrap(), vec![2]);
        assert_eq!(bob.run(8, &removal).unwrap(), vec![3]);
        let new = alice.run(7, b"after removing Bob").unwrap();
        assert!(bob.run(8, &new).is_err());
        assert!(bob.run(7, b"removed sender").is_err());
        assert_eq!(&charlie.run(8, &new).unwrap()[33..], b"after removing Bob");
    }

    #[test]
    fn malformed_packages_and_wrong_group_are_rejected() {
        let (mut alice, mut bob) = pair();
        let mut stranger = Actor::new().unwrap();
        let mut package = stranger.run(2, &[]).unwrap();
        package.push(0);
        assert_eq!(alice.run(4, &package).unwrap_err(), 3);
        assert!(!alice.pending_commit);
        package.pop();
        package[10] ^= 1;
        assert_eq!(alice.run(4, &package).unwrap_err(), 3);
        assert!(!alice.pending_commit);
        let mut other = Actor::new().unwrap();
        other.run(3, &[8; 32]).unwrap();
        let wrong_group = other.run(7, b"different room").unwrap();
        assert!(bob.run(8, &wrong_group).is_err());
        let valid = alice.run(7, b"room still works").unwrap();
        assert_eq!(&bob.run(8, &valid).unwrap()[33..], b"room still works");
    }

    #[test]
    fn membership_operations_require_owner_and_identity_cannot_be_duplicated() {
        let (mut alice, mut bob) = pair();
        assert!(bob.run(9, &fingerprint(alice.signer.public())).is_err());
        assert!(bob.run(4, &[0]).is_err());
        assert!(alice.run(9, &fingerprint(alice.signer.public())).is_err());
        assert!(bob.run(2, &[]).is_err());
        let mut owner = Actor::new().unwrap();
        let mut member = Actor::new().unwrap();
        owner.run(3, &[9; 32]).unwrap();
        let package = member.run(2, &[]).unwrap();
        owner.run(4, &package).unwrap();
        owner.run(6, &[]).unwrap();
        assert!(owner.run(4, &package).is_err());
    }

    #[test]
    fn ratchet_tree_is_rejected_in_leaf_context_before_decoding_its_payload() {
        let forbidden = vec![Extension::Unknown(
            2,
            openmls::extensions::UnknownExtension(vec![0xff; 4096]),
        )];
        let wire = forbidden.tls_serialize_detached().unwrap();
        let error = Extensions::<LeafNode>::tls_deserialize_exact(&wire)
            .err()
            .expect("Forbidden ratchet-tree extension accepted");
        assert!(
            matches!(error, openmls::prelude::tls_codec::Error::DecodingError(ref message) if message.contains("leaf nodes")),
            "{error:?}"
        );
    }

    #[test]
    fn out_of_order_delivery_and_invalid_input_do_not_allow_replays() {
        let (mut alice, mut bob) = pair();
        let first = alice.run(7, b"first").unwrap();
        let second = alice.run(7, b"second").unwrap();
        assert_eq!(&bob.run(8, &second).unwrap()[33..], b"second");
        assert_eq!(&bob.run(8, &first).unwrap()[33..], b"first");
        for length in 0..256 {
            let input: Vec<u8> = (0..length).map(|index| (index % 251) as u8).collect();
            assert!(bob.run(8, &input).is_err());
        }
        assert!(bob.run(8, &first).is_err());
    }

    #[test]
    fn ffi_handles_and_arguments_are_bounded() {
        let mut handle = 0;
        assert_eq!(unsafe { tc_mls_create(&mut handle) }, 0);
        assert_ne!(handle, 0);
        assert_eq!(unsafe { tc_mls_execute(handle, 7, std::ptr::null(), 1) }, 1);
        assert_eq!(
            unsafe { tc_mls_execute(handle, 7, std::ptr::null(), MAX_INPUT + 1) },
            1
        );
        assert_eq!(
            unsafe { tc_mls_execute(handle, 77, std::ptr::null(), 0) },
            1
        );
        assert_eq!(tc_mls_destroy(handle), 0);
        assert_eq!(tc_mls_destroy(handle), 2);
        assert_eq!(unsafe { tc_mls_execute(handle, 1, std::ptr::null(), 0) }, 2);
    }
}
