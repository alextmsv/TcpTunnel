using System;
using System.Collections.Generic;

namespace TCPTunnel
{
    internal enum CommandDisposition
    {
        NotCommand,
        Handled,
        EndSession
    }

    internal enum KickCommandResult
    {
        Success,
        NotFound,
        CannotKickSelf
    }

    internal enum SnakeCommandResult
    {
        Unavailable,
        Paused,
        Resumed
    }

    internal sealed class CommandContext
    {
        public Action ClearChat { get; set; }
        public Action LookImage { get; set; }
        public Action StopLocalHub { get; set; }
        public Action<string, ConsoleColor?> WriteLine { get; set; }
        public Func<string> GetStatus { get; set; }
        public Func<string, string, KickCommandResult> Kick { get; set; }
        public Func<SnakeCommandResult> ToggleSnake { get; set; }
        public bool IsLocalHubAdministrator { get; set; }
    }

    internal static class Commands
    {
        private sealed class ParsedToken
        {
            public string Value;
            public bool WasQuoted;
        }

        public static CommandDisposition InitCommand(string input, CommandContext context)
        {
            if (String.IsNullOrWhiteSpace(input) || input[0] != '/')
                return CommandDisposition.NotCommand;
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            List<ParsedToken> tokens;
            if (!TryTokenize(input, out tokens) || tokens.Count == 0)
            {
                context.WriteLine(Lang.Get(TextId.UnknownCommand, input), ConsoleColor.Yellow);
                return CommandDisposition.Handled;
            }

            string command = tokens[0].Value.ToLowerInvariant();
            switch (command)
            {
                case "/help":
                    if (!HasExactArgumentCount(tokens, 0))
                        return WriteUsage(context, TextId.CommandHelp);
                    context.WriteLine(Lang.Get(TextId.CommandHelp), ConsoleColor.DarkGreen);
                    return CommandDisposition.Handled;

                case "/clear":
                    if (!HasExactArgumentCount(tokens, 0))
                        return WriteUsage(context, TextId.CommandHelp);
                    context.ClearChat();
                    return CommandDisposition.Handled;

                case "/look":
                    if (!HasExactArgumentCount(tokens, 0))
                        return WriteUsage(context, TextId.CommandHelp);
                    context.LookImage();
                    return CommandDisposition.Handled;

                case "/ping":
                    return ExecutePing(tokens, context);

                case "/status":
                    if (!HasExactArgumentCount(tokens, 0))
                        return WriteUsage(context, TextId.CommandHelp);
                    context.WriteLine(context.GetStatus(), null);
                    return CommandDisposition.Handled;

                case "/stop":
                    if (!HasExactArgumentCount(tokens, 0))
                        return WriteUsage(context, TextId.CommandHelp);
                    if (context.IsLocalHubAdministrator)
                    {
                        context.WriteLine(Lang.Get(TextId.StoppingLocalHub), ConsoleColor.Yellow);
                        context.StopLocalHub();
                        return CommandDisposition.EndSession;
                    }

                    SnakeCommandResult snakeResult = context.ToggleSnake();
                    if (snakeResult == SnakeCommandResult.Unavailable)
                        context.WriteLine(Lang.Get(TextId.NoActiveSnake), ConsoleColor.Yellow);
                    else if (snakeResult == SnakeCommandResult.Paused)
                        context.WriteLine(Lang.Get(TextId.SnakePaused), ConsoleColor.Yellow);
                    else
                        context.WriteLine(Lang.Get(TextId.SnakeResumed), ConsoleColor.Green);
                    return CommandDisposition.Handled;

                case "/kick":
                    return ExecuteKick(tokens, context);

                case "/exit":
                    if (!HasExactArgumentCount(tokens, 0))
                        return WriteUsage(context, TextId.CommandHelp);
                    return CommandDisposition.EndSession;

                default:
                    context.WriteLine(Lang.Get(TextId.UnknownCommand, tokens[0].Value), ConsoleColor.Yellow);
                    return CommandDisposition.Handled;
            }
        }

        public static bool RunSelfTest()
        {
            List<ParsedToken> tokens;
            if (!TryTokenize("/kick @alex \"Get out now\"", out tokens) ||
                tokens.Count != 3 || tokens[2].Value != "Get out now" || !tokens[2].WasQuoted)
                return false;
            if (!TryTokenize("/ping localhost:9091", out tokens) || tokens.Count != 2)
                return false;
            if (TryTokenize("/kick @alex \"unterminated", out tokens))
                return false;

            string kickedNickname = null;
            string kickReason = null;
            int writes = 0;
            var context = new CommandContext
            {
                IsLocalHubAdministrator = true,
                ClearChat = () => { },
                LookImage = () => { },
                StopLocalHub = () => { },
                WriteLine = (text, color) => writes++,
                GetStatus = () => "status",
                ToggleSnake = () => SnakeCommandResult.Paused,
                Kick = (nickname, reason) =>
                {
                    kickedNickname = nickname;
                    kickReason = reason;
                    return KickCommandResult.Success;
                }
            };
            if (InitCommand("/kick @alex \"Get out now\"", context) != CommandDisposition.Handled ||
                kickedNickname != "alex" || kickReason != "Get out now" || writes != 1)
                return false;

            string address;
            int port;
            return TryParseEndpoint("localhost:9091", out address, out port) &&
                   address == "localhost" && port == 9091 &&
                   !TryParseEndpoint("localhost:0", out address, out port);
        }

        private static CommandDisposition ExecutePing(List<ParsedToken> tokens, CommandContext context)
        {
            string address;
            int port;
            if (tokens.Count != 2 || !TryParseEndpoint(tokens[1].Value, out address, out port))
                return WriteUsage(context, TextId.PingCommandUsage);

            bool reachable = NetWorker.ping(address, port);
            context.WriteLine(
                Lang.Get(reachable ? TextId.ServerAlive : TextId.ServerDead, address, port),
                reachable ? ConsoleColor.Green : ConsoleColor.Red);
            return CommandDisposition.Handled;
        }

        private static CommandDisposition ExecuteKick(List<ParsedToken> tokens, CommandContext context)
        {
            if (!context.IsLocalHubAdministrator)
            {
                context.WriteLine(Lang.Get(TextId.CommandNoPermission), ConsoleColor.Red);
                return CommandDisposition.Handled;
            }

            if (tokens.Count < 2 || tokens.Count > 3 ||
                tokens[1].WasQuoted ||
                (tokens.Count == 3 && !tokens[2].WasQuoted))
                return WriteUsage(context, TextId.KickUsage);

            string nickname = tokens[1].Value;
            if (nickname.StartsWith("@", StringComparison.Ordinal))
                nickname = nickname.Substring(1);
            if (!NetWorker.IsNicknameValid(nickname))
                return WriteUsage(context, TextId.KickUsage);

            string reason = tokens.Count == 3 ? SanitizeReason(tokens[2].Value) : String.Empty;
            if (reason == null)
                return WriteUsage(context, TextId.KickUsage);

            KickCommandResult result = context.Kick(nickname, reason);
            if (result == KickCommandResult.CannotKickSelf)
                context.WriteLine(Lang.Get(TextId.KickCannotSelf), ConsoleColor.Yellow);
            else if (result == KickCommandResult.NotFound)
                context.WriteLine(Lang.Get(TextId.KickUserNotFound, nickname), ConsoleColor.Yellow);
            else
                context.WriteLine(Lang.Get(TextId.KickSucceeded, nickname), ConsoleColor.Green);
            return CommandDisposition.Handled;
        }

        private static CommandDisposition WriteUsage(CommandContext context, TextId usage)
        {
            context.WriteLine(Lang.Get(usage), ConsoleColor.Yellow);
            return CommandDisposition.Handled;
        }

        private static bool HasExactArgumentCount(List<ParsedToken> tokens, int arguments)
        {
            return tokens.Count == arguments + 1;
        }

        private static string SanitizeReason(string reason)
        {
            if (reason == null || reason.Length > 200)
                return null;

            char[] characters = reason.Trim().ToCharArray();
            for (int index = 0; index < characters.Length; index++)
            {
                if (Char.IsControl(characters[index]))
                    characters[index] = ' ';
            }
            return new string(characters);
        }

        private static bool TryParseEndpoint(string argument, out string address, out int port)
        {
            address = null;
            port = 0;
            if (String.IsNullOrWhiteSpace(argument))
                return false;

            int separator = argument.LastIndexOf(':');
            if (separator <= 0 || separator == argument.Length - 1)
                return false;

            string parsedAddress = argument.Substring(0, separator).Trim();
            int parsedPort;
            if (parsedAddress.Length == 0 ||
                !Int32.TryParse(argument.Substring(separator + 1), out parsedPort) ||
                parsedPort < 1 || parsedPort > 65535)
                return false;

            address = parsedAddress;
            port = parsedPort;
            return true;
        }

        private static bool TryTokenize(string input, out List<ParsedToken> tokens)
        {
            tokens = new List<ParsedToken>();
            int index = 0;
            while (index < input.Length)
            {
                while (index < input.Length && Char.IsWhiteSpace(input[index]))
                    index++;
                if (index >= input.Length)
                    break;

                bool quoted = input[index] == '"';
                if (quoted)
                    index++;
                var value = new System.Text.StringBuilder();
                bool closed = !quoted;
                while (index < input.Length)
                {
                    char character = input[index++];
                    if (quoted && character == '"')
                    {
                        closed = true;
                        break;
                    }
                    if (!quoted && Char.IsWhiteSpace(character))
                        break;
                    if (character == '\\' && index < input.Length &&
                        (input[index] == '"' || input[index] == '\\'))
                        character = input[index++];
                    value.Append(character);
                }

                if (!closed)
                    return false;
                if (quoted && index < input.Length && !Char.IsWhiteSpace(input[index]))
                    return false;

                tokens.Add(new ParsedToken { Value = value.ToString(), WasQuoted = quoted });
            }
            return true;
        }
    }
}
