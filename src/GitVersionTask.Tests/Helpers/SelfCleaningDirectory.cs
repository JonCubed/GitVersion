﻿using System;
using System.IO;

public class SelfCleaningDirectory
{
    public SelfCleaningDirectory(IPostTestDirectoryRemover directoryRemover, string path)
    {
        if (Directory.Exists(path))
        {
            throw new InvalidOperationException($"Directory '{path}' already exists.");
        }

        DirectoryPath = path;
        directoryRemover.Register(DirectoryPath);
    }

    public string DirectoryPath;
}