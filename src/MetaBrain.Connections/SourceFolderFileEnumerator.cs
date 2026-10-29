using System.Text.RegularExpressions;
using MetaBrain.Core.Domain;
using MetaBrain.Core.Security;

namespace MetaBrain.Connections;

/// <summary>
/// Enumerates one owner-selected root without opening file contents or
/// following any reparse point.
/// </summary>
internal sealed class SourceFolderFileEnumerator : ISourceFolderEnumerator
{
    private const int MaximumEntries = SourceFolderEnrollmentGrammar.MaximumPreviewEntries;

    public SourceFolderEnumeration Enumerate(
        string rootLocator,
        bool recursive,
        string[] includePatterns,
        string[] excludePatterns,
        CancellationToken cancellationToken)
    {
        var root = SourceFolderEnrollmentGrammar.NormalizeRoot(rootLocator);
        var includes = SourceFolderEnrollmentGrammar.NormalizePatterns(includePatterns, required: true)
            .Select(CompilePattern)
            .ToArray();
        var excludes = SourceFolderEnrollmentGrammar.NormalizePatterns(excludePatterns, required: false)
            .Select(CompilePattern)
            .ToArray();

        var rootAttributes = GetRootAttributesWithoutReparseAncestors(root);

        if ((rootAttributes & FileAttributes.Directory) == 0)
        {
            throw new SourceOperationException("source_folder_missing");
        }

        if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new SourceOperationException("source_folder_reparse_point");
        }
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(root);
        var entries = new List<SourceFolderEntry>();
        var visitedEntries = 0;
        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pendingDirectories.Pop();
            FileAttributes directoryAttributes;
            try
            {
                directoryAttributes = File.GetAttributes(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       System.Security.SecurityException or ArgumentException or NotSupportedException)
            {
                throw new SourceOperationException("source_folder_changed");
            }

            if ((directoryAttributes & FileAttributes.ReparsePoint) != 0)
            {
                if (string.Equals(directory, root, StringComparison.OrdinalIgnoreCase))
                {
                    throw new SourceOperationException("source_folder_reparse_point");
                }

                var relativeDirectory = SourceFolderEnrollmentGrammar.NormalizeRelativePath(Path.GetRelativePath(root, directory));
                AddEntry(entries, new SourceFolderEntry(relativeDirectory, directory, Included: false,
                    ExclusionReason: "excluded_reparse_point"));
                continue;
            }

            if ((directoryAttributes & FileAttributes.Directory) == 0)
            {
                throw new SourceOperationException("source_folder_changed");
            }
            try
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++visitedEntries > MaximumEntries)
                    {
                        throw new SourceOperationException("source_folder_limit");
                    }

                    var fullPath = Path.GetFullPath(child);
                    if (!SourceFolderEnrollmentGrammar.IsWithinRoot(root, fullPath))
                    {
                        throw new SourceOperationException("source_folder_outside_root");
                    }

                    FileAttributes attributes;
                    try
                    {
                        attributes = File.GetAttributes(fullPath);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                               System.Security.SecurityException or ArgumentException or NotSupportedException)
                    {
                        throw new SourceOperationException("source_folder_changed");
                    }

                    var relative = SourceFolderEnrollmentGrammar.NormalizeRelativePath(Path.GetRelativePath(root, fullPath));
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        AddEntry(entries, new SourceFolderEntry(relative, fullPath, Included: false,
                            ExclusionReason: "excluded_reparse_point"));
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (recursive)
                        {
                            pendingDirectories.Push(fullPath);
                        }

                        continue;
                    }

                    var excluded = MatchesAny(excludes, relative);
                    var included = !excluded && MatchesAny(includes, relative);
                    if (included && fullPath.Length > RegisteredSourceCodec.MaximumLocatorLength)
                    {
                        throw new SourceOperationException("source_folder_registry_locator_too_long");
                    }

                    AddEntry(entries, new SourceFolderEntry(
                        relative,
                        fullPath,
                        included,
                        included ? null : excluded ? "excluded_by_pattern" : "excluded_not_included"));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       System.Security.SecurityException or ArgumentException or NotSupportedException)
            {
                throw new SourceOperationException("source_folder_unreadable");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        entries.Sort(static (left, right) =>
        {
            var result = StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath);
            return result != 0 ? result : StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath);
        });
        return new SourceFolderEnumeration(root, entries.ToArray());
    }

    private static void AddEntry(List<SourceFolderEntry> entries, SourceFolderEntry entry)
    {
        if (entries.Count >= MaximumEntries)
        {
            throw new SourceOperationException("source_folder_limit");
        }

        entries.Add(entry);
    }

    private static Regex CompilePattern(string pattern)
    {
        var normalized = pattern.Replace('\\', '/');
        var expression = Regex.Escape(normalized)
            .Replace(@"\*\*/", @"(?:.*/)?", StringComparison.Ordinal)
            .Replace(@"\*\*", ".*", StringComparison.Ordinal)
            .Replace(@"\*", "[^/]*", StringComparison.Ordinal)
            .Replace(@"\?", "[^/]", StringComparison.Ordinal);
        if (!normalized.Contains('/'))
        {
            expression = @"(?:.*/)?" + expression;
        }

        return new Regex(@"\A" + expression + @"\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
            TimeSpan.FromMilliseconds(50));
    }

    private static bool MatchesAny(IReadOnlyList<Regex> patterns, string relativePath)
    {
        foreach (var pattern in patterns)
        {
            if (pattern.IsMatch(relativePath))
            {
                return true;
            }
        }

        return false;
    }

    private static FileAttributes GetRootAttributesWithoutReparseAncestors(string root)
    {
        var volumeRoot = Path.GetPathRoot(root);
        if (string.IsNullOrEmpty(volumeRoot))
        {
            throw new SourceOperationException("source_folder_missing");
        }

        var volumeRootComparison = volumeRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var ancestors = new Stack<string>();
        var current = Path.TrimEndingDirectorySeparator(root);
        while (!string.Equals(
                   current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                   volumeRootComparison,
                   StringComparison.OrdinalIgnoreCase))
        {
            ancestors.Push(current);
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                throw new SourceOperationException("source_folder_missing");
            }

            current = Path.TrimEndingDirectorySeparator(parent);
        }

        var attributes = ReadRootPathAttributes(current);
        EnsureRootPathComponentIsDirectory(attributes, ancestors.Count > 0);
        while (ancestors.Count > 0)
        {
            current = ancestors.Pop();
            attributes = ReadRootPathAttributes(current);
            EnsureRootPathComponentIsDirectory(attributes, ancestors.Count > 0);
        }

        return attributes;
    }

    private static FileAttributes ReadRootPathAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            throw new SourceOperationException("source_folder_missing");
        }
        catch (DirectoryNotFoundException)
        {
            throw new SourceOperationException("source_folder_missing");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            throw new SourceOperationException("source_folder_unreadable");
        }
    }

    private static void EnsureRootPathComponentIsDirectory(FileAttributes attributes, bool mustBeDirectory)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new SourceOperationException("source_folder_reparse_point");
        }

        if ((attributes & FileAttributes.Directory) == 0 && mustBeDirectory)
        {
            throw new SourceOperationException("source_folder_missing");
        }
    }

}
