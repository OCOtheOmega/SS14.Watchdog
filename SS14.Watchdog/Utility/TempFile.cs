using System.IO;

namespace SS14.Watchdog.Utility
{
    public static class TempFile
    {
        public static FileStream CreateTempFile(string? directory = null)
        {
            directory ??= Path.GetTempPath();
            Directory.CreateDirectory(directory);
            return new TempFileStream(Path.Combine(directory, $".update-download-{System.Guid.NewGuid():N}.tmp"));
        }

        private sealed class TempFileStream : FileStream
        {
            private readonly System.IDisposable _active;

            public TempFileStream(string path) : base(path, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose)
            {
                _active = Components.Updates.InstanceStorage.Protect(path);
            }

            protected override void Dispose(bool disposing)
            {
                base.Dispose(disposing);
                
                if (disposing)
                    _active.Dispose();
            }

            public override async System.Threading.Tasks.ValueTask DisposeAsync()
            {
                await base.DisposeAsync();
                _active.Dispose();
            }
        } 
    }
}
