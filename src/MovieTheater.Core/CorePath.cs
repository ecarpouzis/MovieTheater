using System;
using System.IO;
using System.Linq;

namespace MovieTheater.Core
{
    public static class CorePath
    {
        private static DirectoryInfo AssemblyRootDir { get; }

        public static DirectoryInfo TempDirectoryRoot { get; }

        static CorePath()
        {
            var assemblyLocation = System.Reflection.Assembly.GetEntryAssembly().Location;
            var assemblyFile = new FileInfo(assemblyLocation);
            AssemblyRootDir = assemblyFile.Directory;

            TempDirectoryRoot = LocalDir("temp");
            TempDirectoryRoot.Create();
        }

        public static DirectoryInfo LocalDir(params string[] paths)
        {
            paths = paths.Prepend(AssemblyRootDir.FullName).ToArray();
            return new DirectoryInfo(Path.Combine(paths));
        }

        public static FileInfo LocalFile(params string[] paths)
        {
            paths = paths.Prepend(AssemblyRootDir.FullName).ToArray();
            return new FileInfo(Path.Combine(paths));
        }

        public static DirectoryInfo TempDir(params string[] paths)
        {
            paths = paths.Prepend(TempDirectoryRoot.FullName).ToArray();
            return new DirectoryInfo(Path.Combine(paths));
        }

        public static FileInfo TempFile(params string[] paths)
        {
            paths = paths.Prepend(TempDirectoryRoot.FullName).ToArray();
            return new FileInfo(Path.Combine(paths));
        }

        public static IDisposableTempFileInfo DisposableTempFile()
        {
            TempDir("disposable").Create();

            FileInfo fileInfo;
            int tries = 0;

            do
            {
                fileInfo = TempFile("disposable", Path.GetRandomFileName());
                tries++;

                if (tries >= 100)
                    throw new InvalidOperationException("Too many tries");
            } while (fileInfo.Exists);

            return new DisposableTempFileObject(fileInfo);
        }

        public interface IDisposableTempFileInfo : IDisposable
        {
            FileInfo FileInfo { get; }
        }

        private class DisposableTempFileObject : IDisposableTempFileInfo
        {
            public FileInfo FileInfo { get; }

            public DisposableTempFileObject(FileInfo fileInfo)
            {
                this.FileInfo = fileInfo;
            }

            public void Dispose()
            {
                if (FileInfo.Exists)
                    FileInfo.Delete();
            }
        }
    }
}
