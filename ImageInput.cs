using System;
using System.IO;

namespace TCPTunnel
{
    internal enum ImageInputKind
    {
        NotImage,
        SupportedImage,
        WebPImage
    }

    internal static class ImageInput
    {
        public static ImageInputKind Classify(string input, out string path)
        {
            return Classify(input, File.Exists, out path);
        }

        internal static ImageInputKind Classify(string input, Func<string, bool> fileExists, out string path)
        {
            path = null;
            if (String.IsNullOrWhiteSpace(input) || fileExists == null)
                return ImageInputKind.NotImage;

            string candidate = input.Trim();
            if (candidate.StartsWith("/", StringComparison.Ordinal))
                return ImageInputKind.NotImage;
            if (candidate.Length >= 2 && candidate[0] == '"' && candidate[candidate.Length - 1] == '"')
                candidate = candidate.Substring(1, candidate.Length - 2);
            else if (candidate.IndexOf('"') >= 0)
                return ImageInputKind.NotImage;

            if (candidate.Length == 0 || !fileExists(candidate))
                return ImageInputKind.NotImage;

            string extension = Path.GetExtension(candidate);
            ImageInputKind kind;
            if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
                kind = ImageInputKind.SupportedImage;
            else if (extension.Equals(".webp", StringComparison.OrdinalIgnoreCase))
                kind = ImageInputKind.WebPImage;
            else
                return ImageInputKind.NotImage;

            try { path = Path.GetFullPath(candidate); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return ImageInputKind.NotImage;
            }
            return kind;
        }

        internal static bool RunSelfTest()
        {
            Func<string, bool> exists = value =>
                value.Equals(@"C:\x\a.jpg", StringComparison.OrdinalIgnoreCase) ||
                value.Equals(@"C:\x\a.jpeg", StringComparison.OrdinalIgnoreCase) ||
                value.Equals(@"C:\x\a.png", StringComparison.OrdinalIgnoreCase) ||
                value.Equals(@"C:\my images\a.png", StringComparison.OrdinalIgnoreCase) ||
                value.Equals(@"C:\x\a.webp", StringComparison.OrdinalIgnoreCase);
            string path;
            return Classify(@"C:\x\a.JPG", exists, out path) == ImageInputKind.SupportedImage &&
                   Classify(@"C:\x\a.JPEG", exists, out path) == ImageInputKind.SupportedImage &&
                   Classify(@"C:\x\a.PNG", exists, out path) == ImageInputKind.SupportedImage &&
                   Classify("\"C:\\my images\\a.png\"", exists, out path) == ImageInputKind.SupportedImage &&
                   Classify(@"C:\x\a.webp", exists, out path) == ImageInputKind.WebPImage &&
                   Classify(@"C:\missing.png", exists, out path) == ImageInputKind.NotImage &&
                   Classify(@"/ping C:\x\a.jpg", exists, out path) == ImageInputKind.NotImage &&
                   Classify(@"look C:\x\a.jpg", exists, out path) == ImageInputKind.NotImage &&
                   Classify("\"C:\\x\\a.jpg\" \"C:\\x\\b.jpg\"", exists, out path) == ImageInputKind.NotImage;
        }
    }
}
