﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Umbraco.Core.IO
{
    internal class ShadowFileSystem : IFileSystem2
    {
        private readonly IFileSystem _fs;
        private readonly IFileSystem2 _sfs;

        public ShadowFileSystem(IFileSystem fs, IFileSystem2 sfs)
        {
            _fs = fs;
            _sfs = sfs;
        }

        public IFileSystem Inner
        {
            get { return _fs; }
        }

        public void Complete()
        {
            if (_nodes == null) return;
            var exceptions = new List<Exception>();
            foreach (var kvp in _nodes)
            {
                if (kvp.Value.IsExist)
                {
                    if (kvp.Value.IsFile)
                    {
                        try
                        {
                            using (var stream = _sfs.OpenFile(kvp.Key))
                                _fs.AddFile(kvp.Key, stream, true);
                        }
                        catch (Exception e)
                        {
                            exceptions.Add(new Exception("Could not save file \"" + kvp.Key + "\".", e));
                        }
                    }
                }
                else
                {
                    try
                    {
                        if (kvp.Value.IsDir)
                            _fs.DeleteDirectory(kvp.Key, true);
                        else
                            _fs.DeleteFile(kvp.Key);
                    }
                    catch (Exception e)
                    {
                        exceptions.Add(new Exception("Could not delete " + (kvp.Value.IsDir ? "directory": "file") + " \"" + kvp.Key + "\".", e));
                    }
                }
            }
            _nodes.Clear();

            if (exceptions.Count == 0) return;
            throw new AggregateException("Failed to apply all changes (see exceptions).", exceptions);
        }

        private Dictionary<string, ShadowNode> _nodes;

        private Dictionary<string, ShadowNode> Nodes {  get { return _nodes ?? (_nodes = new Dictionary<string, ShadowNode>()); } }

        private class ShadowNode
        {
            public ShadowNode(bool isDelete, bool isdir)
            {
                IsDelete = isDelete;
                IsDir = isdir;
            }

            public bool IsDelete { get; private set; }
            public bool IsDir { get; private set; }

            public bool IsExist { get { return IsDelete == false; } }
            public bool IsFile { get { return IsDir == false; } }
        }

        private static string NormPath(string path)
        {
            return path.ToLowerInvariant().Replace("\\", "/");
        }

        // values can be "" (root), "foo", "foo/bar"...
        private static bool IsChild(string path, string input)
        {
            if (input.StartsWith(path) == false || input.Length < path.Length + 2)
                return false;
            if (path.Length > 0 && input[path.Length] != '/') return false;
            var pos = input.IndexOf("/", path.Length + 1, StringComparison.OrdinalIgnoreCase);
            return pos < 0;
        }

        private static bool IsDescendant(string path, string input)
        {
            if (input.StartsWith(path) == false || input.Length < path.Length + 2)
                return false;
            return path.Length == 0 || input[path.Length] == '/';
        }

        public IEnumerable<string> GetDirectories(string path)
        {
            var normPath = NormPath(path);
            var shadows = Nodes.Where(kvp => IsChild(normPath, kvp.Key)).ToArray();
            var directories = _fs.GetDirectories(path);
            return directories
                .Except(shadows.Where(kvp => (kvp.Value.IsDir && kvp.Value.IsDelete) || (kvp.Value.IsFile && kvp.Value.IsExist))
                    .Select(kvp => kvp.Key))
                .Union(shadows.Where(kvp => kvp.Value.IsDir && kvp.Value.IsExist).Select(kvp => kvp.Key))
                .Distinct();
        }

        public void DeleteDirectory(string path)
        {
            DeleteDirectory(path, false);
        }

        public void DeleteDirectory(string path, bool recursive)
        {
            if (DirectoryExists(path) == false) return;
            var normPath = NormPath(path);
            if (recursive)
            {
                Nodes[normPath] = new ShadowNode(true, true);
                var remove = Nodes.Where(x => IsDescendant(normPath, x.Key)).ToList();
                foreach (var kvp in remove) Nodes.Remove(kvp.Key);
                Delete(path, true);
            }
            else
            {
                if (Nodes.Any(x => IsChild(normPath, x.Key) && x.Value.IsExist) // shadow content
                    || _fs.GetDirectories(path).Any() || _fs.GetFiles(path).Any()) // actual content
                    throw new InvalidOperationException("Directory is not empty.");
                Nodes[path] = new ShadowNode(true, true);
                var remove = Nodes.Where(x => IsChild(normPath, x.Key)).ToList();
                foreach (var kvp in remove) Nodes.Remove(kvp.Key);
                Delete(path, false);
            }
        }

        private void Delete(string path, bool recurse)
        {
            foreach (var file in _fs.GetFiles(path))
            {
                Nodes[NormPath(file)] = new ShadowNode(true, false);
            }
            foreach (var dir in _fs.GetDirectories(path))
            {
                Nodes[NormPath(dir)] = new ShadowNode(true, true);
                if (recurse) Delete(dir, true);
            }
        }

        public bool DirectoryExists(string path)
        {
            ShadowNode sf;
            if (Nodes.TryGetValue(NormPath(path), out sf))
                return sf.IsDir && sf.IsExist;
            return _fs.DirectoryExists(path);
        }

        public void AddFile(string path, Stream stream)
        {
            AddFile(path, stream, true);
        }

        public void AddFile(string path, Stream stream, bool overrideIfExists)
        {
            ShadowNode sf;
            var normPath = NormPath(path);
            if (Nodes.TryGetValue(normPath, out sf) && sf.IsExist && (sf.IsDir || overrideIfExists == false))
                throw new InvalidOperationException(string.Format("A file at path '{0}' already exists", path));

            var parts = normPath.Split('/');
            for (var i = 0; i < parts.Length - 1; i++)
            {
                var dirPath = string.Join("/", parts.Take(i + 1));
                ShadowNode sd;
                if (Nodes.TryGetValue(dirPath, out sd))
                {
                    if (sd.IsFile) throw new InvalidOperationException("Invalid path.");
                    if (sd.IsDelete) Nodes[dirPath] = new ShadowNode(false, true);
                }
                else
                {
                    if (_fs.DirectoryExists(dirPath)) continue;
                    if (_fs.FileExists(dirPath)) throw new InvalidOperationException("Invalid path.");
                    Nodes[dirPath] = new ShadowNode(false, true);
                }
            }

            _sfs.AddFile(path, stream, overrideIfExists);
            Nodes[normPath] = new ShadowNode(false, false);
        }

        public IEnumerable<string> GetFiles(string path)
        {
            var normPath = NormPath(path);
            var shadows = Nodes.Where(kvp => IsChild(normPath, kvp.Key)).ToArray();
            var files = _fs.GetFiles(path);
            return files
                .Except(shadows.Where(kvp => (kvp.Value.IsFile && kvp.Value.IsDelete) || kvp.Value.IsDir)
                    .Select(kvp => kvp.Key))
                .Union(shadows.Where(kvp => kvp.Value.IsFile && kvp.Value.IsExist).Select(kvp => kvp.Key))
                .Distinct();
        }

        public IEnumerable<string> GetFiles(string path, string filter)
        {
            return _fs.GetFiles(path, filter);
        }

        public Stream OpenFile(string path)
        {
            ShadowNode sf;
            if (Nodes.TryGetValue(NormPath(path), out sf))
                return sf.IsDir || sf.IsDelete ? null : _sfs.OpenFile(path);
            return _fs.OpenFile(path);
        }

        public void DeleteFile(string path)
        {
            if (FileExists(path) == false) return;
            Nodes[NormPath(path)] = new ShadowNode(true, false);
        }

        public bool FileExists(string path)
        {
            ShadowNode sf;
            if (Nodes.TryGetValue(NormPath(path), out sf))
                return sf.IsFile && sf.IsExist;
            return _fs.FileExists(path);
        }

        public string GetRelativePath(string fullPathOrUrl)
        {
            return _fs.GetRelativePath(fullPathOrUrl);
        }

        public string GetFullPath(string path)
        {
            return _fs.GetFullPath(path);
        }

        public string GetUrl(string path)
        {
            return _fs.GetUrl(path);
        }

        public DateTimeOffset GetLastModified(string path)
        {
            ShadowNode sf;
            if (Nodes.TryGetValue(NormPath(path), out sf) == false) return _fs.GetLastModified(path);
            if (sf.IsDelete) throw new InvalidOperationException("Invalid path.");
            return _sfs.GetLastModified(path);
        }

        public DateTimeOffset GetCreated(string path)
        {
            ShadowNode sf;
            if (Nodes.TryGetValue(NormPath(path), out sf) == false) return _fs.GetCreated(path);
            if (sf.IsDelete) throw new InvalidOperationException("Invalid path.");
            return _sfs.GetCreated(path);
        }

        public long GetSize(string path)
        {
            ShadowNode sf;
            if (Nodes.TryGetValue(NormPath(path), out sf) == false)
            {
                // the inner filesystem (_fs) can be IFileSystem2... or just IFileSystem
                // figure it out and use the most effective GetSize method
                var fs2 = _fs as IFileSystem2;
                return fs2 == null ? _fs.GetSize(path) : fs2.GetSize(path);
            }
            if (sf.IsDelete || sf.IsDir) throw new InvalidOperationException("Invalid path.");
            return _sfs.GetSize(path);
        }
    }
}