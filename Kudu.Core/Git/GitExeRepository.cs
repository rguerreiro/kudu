﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Kudu.Core.Infrastructure;

namespace Kudu.Core.Git {
    /// <summary>
    /// Implementation of a git repository over git.exe
    /// </summary>
    public class GitExeRepository : IRepository {
        private readonly Executable _gitExe;

        public GitExeRepository(string path)
            : this(ResolveGitPath(), path) {

        }

        public GitExeRepository(string pathToGitExe, string path) {
            _gitExe = new Executable(pathToGitExe, path);
        }

        public string CurrentId {
            get {
                return _gitExe.Execute("rev-parse HEAD").Trim();
            }
        }

        public void Initialize() {
            _gitExe.Execute("init");
            _gitExe.Execute("config core.autocrlf true");
        }

        public IEnumerable<FileStatus> GetStatus() {
            string status = _gitExe.Execute("status --porcelain");
            return ParseStatus(status.AsReader());
        }

        public IEnumerable<ChangeSet> GetChanges() {
            return Log();
        }

        public IEnumerable<ChangeSet> GetChanges(int index, int limit) {
            return Log("log --all --skip {0} -n {1}", index, limit);
        }

        public void AddFile(string path) {
            _gitExe.Execute("add {0}", path);
        }

        public void RemoveFile(string path) {
            _gitExe.Execute("rm {0} --cached", path);
        }

        public ChangeSet Commit(string authorName, string message) {
            // Add all unstaged files
            _gitExe.Execute("add -A");
            string output = _gitExe.Execute("commit -m\"{0}\" --author=\"{1}\"", message, authorName);

            // No pending changes
            if (output.Contains("working directory clean")) {
                return null;
            }

            string newCommit = _gitExe.Execute("show HEAD");
            return ParseCommit(newCommit.AsReader());
        }

        public void Update(string id) {
            _gitExe.Execute("checkout {0} --force", id);
        }

        public ChangeSetDetail GetDetails(string id) {
            string show = _gitExe.Execute("show {0} -m -p --numstat --shortstat", id);
            var detail = ParseShow(show.AsReader());

            string showStatus = _gitExe.Execute("show {0} -m --name-status --format=\"%H\"", id);
            var statusReader = showStatus.AsReader();

            // Skip the commit details
            statusReader.ReadLine();
            statusReader.SkipWhitespace();
            PopulateStatus(statusReader, detail);

            return detail;
        }

        public ChangeSetDetail GetWorkingChanges() {
            if (!GetStatus().Any()) {
                return null;
            }

            // Add everything so we can see a diff of the current changes
            _gitExe.Execute("add -A");

            string diff = _gitExe.Execute("diff --no-ext-diff -p --numstat --shortstat --staged");
            var detail = ParseShow(diff.AsReader(), includeChangeSet: false);

            string status = _gitExe.Execute("diff --name-status --staged");
            PopulateStatus(status.AsReader(), detail);

            return detail;
        }

        public IEnumerable<Branch> GetBranches() {
            string branches = _gitExe.Execute("branch");
            var reader = branches.AsReader();

            var branchNames = new List<string>();
            while (!reader.Done) {
                // * branchname
                var lineReader = reader.ReadLine().AsReader();
                lineReader.ReadUntilWhitespace();
                lineReader.SkipWhitespace();
                string branchName = lineReader.ReadToEnd();
                if (branchName.Contains("(no branch)")) {
                    continue;
                }
                branchNames.Add(branchName.Trim());
            }

            foreach (var branchName in branchNames) {
                string id = _gitExe.Execute("rev-parse {0}", branchName);
                yield return new Branch(id.Trim(), branchName);
            }
        }

        private bool IsEmpty() {
            // REVIEW: Is this reliable
            return String.IsNullOrWhiteSpace(_gitExe.Execute("branch"));
        }

        private static string ResolveGitPath() {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string path = Path.Combine(programFiles, "Git", "bin", "git.exe");

            if (!File.Exists(path)) {
                throw new InvalidOperationException("Unable to locate git.exe");
            }

            return path;
        }

        private IEnumerable<ChangeSet> Log(string command = "log --all", params object[] args) {
            if (IsEmpty()) {
                return Enumerable.Empty<ChangeSet>();
            }

            string log = _gitExe.Execute(command, args);
            return ParseCommits(log.AsReader());
        }

        private IEnumerable<ChangeSet> ParseCommits(IStringReader reader) {
            while (!reader.Done) {
                yield return ParseCommit(reader);
            }
        }

        private void PopulateStatus(IStringReader reader, ChangeSetDetail detail) {
            while (!reader.Done) {
                string line = reader.ReadLine();
                // Status lines contain tabs
                if (!line.Contains("\t")) {
                    continue;
                }
                var lineReader = line.AsReader();
                string status = lineReader.ReadUntilWhitespace();
                lineReader.SkipWhitespace();
                string name = lineReader.ReadUntilWhitespace();
                lineReader.SkipWhitespace();
                FileInfo file;
                if (detail.Files.TryGetValue(name, out file)) {
                    file.Status = ConvertStatus(status);
                }
            }
        }

        private static bool IsCommitHeader(string value) {
            return value.StartsWith("commit ");
        }

        private ChangeSetDetail ParseShow(IStringReader reader, bool includeChangeSet = true) {
            ChangeSetDetail detail = null;
            if (includeChangeSet) {
                detail = ParseCommitAndSummary(reader);
            }
            else {
                detail = new ChangeSetDetail();
                ParseSummary(reader, detail);
            }

            ParseDiffAndPopulate(reader, detail);

            return detail;
        }

        internal static void ParseDiffAndPopulate(IStringReader reader, ChangeSetDetail detail) {
            foreach (var diff in ParseDiff(reader)) {
                FileInfo stats;
                if (!detail.Files.TryGetValue(diff.FileName, out stats)) {
                    stats = new FileInfo();
                    detail.Files.Add(diff.FileName, stats);
                }

                // Set the binary flag if any of the files are binary
                bool binary = diff.Binary || stats.Binary;
                stats.Binary = binary;
                diff.Binary = binary;

                foreach (var line in diff.Lines) {
                    stats.DiffLines.Add(line);
                }
            }
        }

        private static ChangeSetDetail ParseCommitAndSummary(IStringReader reader) {
            // Parse the changeset
            ChangeSet changeSet = ParseCommit(reader);

            var detail = new ChangeSetDetail(changeSet);

            ParseSummary(reader, detail);

            return detail;
        }

        private static void ParseSummary(IStringReader reader, ChangeSetDetail detail) {
            while (!reader.Done) {
                string line = reader.ReadLine();

                if (ParserHelpers.IsSingleLineFeed(line)) {
                    break;
                }
                else if (line.Contains('\t')) {
                    // n	n	path
                    string[] parts = line.Split('\t');
                    int insertions;
                    Int32.TryParse(parts[0], out insertions);
                    int deletions;
                    Int32.TryParse(parts[1], out deletions);
                    string path = parts[2].Trim();

                    detail.Files[path] = new FileInfo {
                        Insertions = insertions,
                        Deletions = deletions,
                        Binary = parts[0] == "-" && parts[1] == "-"
                    };
                }
                else {
                    // n files changed, n insertions(+), n deletions(-)
                    ParserHelpers.ParseSummaryFooter(line, detail);
                }
            }
        }

        internal static IEnumerable<FileDiff> ParseDiff(IStringReader reader) {
            var builder = new StringBuilder();

            // If this was a merge change set then we'll parse the details out of the
            // first diff
            ChangeSetDetail merge = null;

            do {
                string line = reader.ReadLine();

                // If we see a new diff header then process the previous diff if any
                if ((reader.Done || IsDiffHeader(line)) && builder.Length > 0) {
                    if (reader.Done) {
                        builder.Append(line);
                    }
                    string diffChunk = builder.ToString();
                    FileDiff diff = ParseDiffChunk(diffChunk.AsReader(), ref merge);

                    if (diff != null) {
                        yield return diff;
                    }

                    builder.Clear();
                }

                if (!reader.Done) {
                    builder.Append(line);
                }
            } while (!reader.Done);
        }

        private static bool IsDiffHeader(string line) {
            return line.StartsWith("diff --git", StringComparison.InvariantCulture);
        }

        private static FileDiff ParseDiffChunk(IStringReader reader, ref ChangeSetDetail merge) {
            // Extract the file name from the diff header
            var headerReader = reader.ReadLine().AsReader();
            headerReader.ReadUntil("a/");
            headerReader.Skip(2);
            string fileName = headerReader.ReadUntilWhitespace();

            // Skip files from merged changesets
            if (merge != null && merge.Files.ContainsKey(fileName)) {
                return null;
            }

            var diff = new FileDiff(fileName);

            while (!reader.Done) {
                string line = reader.ReadLine();
                if (line.StartsWith("@@")) {
                    reader.PutBack(line.Length);
                    break;
                }
                else if (line.StartsWith("GIT binary patch")) {
                    diff.Binary = true;
                    // Skip binary files
                    reader.ReadToEnd();
                }
            }

            // Current diff range
            DiffRange currentRange = null;
            int? leftCounter = null;
            int? rightCounter = null;

            // Parse the file diff
            while (!reader.Done) {
                int? currentLeft = null;
                int? currentRight = null;
                string line = reader.ReadLine();
                bool isDiffRange = line.StartsWith("@@");
                ChangeType? changeType = null;

                if (line.StartsWith("+")) {
                    changeType = ChangeType.Added;
                    currentRight = ++rightCounter;
                    currentLeft = null;
                }
                else if (line.StartsWith("-")) {
                    changeType = ChangeType.Deleted;
                    currentLeft = ++leftCounter;
                    currentRight = null;
                }
                else if (IsCommitHeader(line)) {
                    reader.PutBack(line.Length);
                    merge = ParseCommitAndSummary(reader);
                }
                else {
                    if (!isDiffRange) {
                        currentLeft = ++leftCounter;
                        currentRight = ++rightCounter;
                    }
                    changeType = ChangeType.None;
                }

                if (changeType != null) {
                    var lineDiff = new LineDiff(changeType.Value, line);
                    if (!isDiffRange) {
                        lineDiff.LeftLine = currentLeft;
                        lineDiff.RightLine = currentRight;
                    }

                    diff.Lines.Add(lineDiff);
                }

                if (isDiffRange) {
                    // Parse the new diff range
                    currentRange = DiffRange.Parse(line.AsReader());
                    leftCounter = currentRange.LeftFrom - 1;
                    rightCounter = currentRange.RightFrom - 1;
                }
            }

            return diff;
        }

        private static ChangeSet ParseCommit(IStringReader reader) {
            // commit hash
            reader.ReadUntilWhitespace();
            reader.SkipWhitespace();
            string id = reader.ReadUntilWhitespace();

            // Merges will have (from hash) so we're skipping that
            reader.ReadLine();

            string author = null;
            string email = null;
            string date = null;

            while (!reader.Done) {
                string line = reader.ReadLine();

                if (ParserHelpers.IsSingleLineFeed(line)) {
                    break;
                }

                var subReader = line.AsReader();
                string key = subReader.ReadUntil(':');
                // Skip :
                subReader.Skip();
                subReader.SkipWhitespace();
                string value = subReader.ReadToEnd().Trim();

                if (key.Equals("Author", StringComparison.OrdinalIgnoreCase)) {
                    // Author <email>
                    var authorReader = value.AsReader();
                    author = authorReader.ReadUntil('<').Trim();
                    authorReader.Skip();
                    email = authorReader.ReadUntil('>');
                }
                else if (key.Equals("Date", StringComparison.OrdinalIgnoreCase)) {
                    date = value;
                }
            }

            var messageBuilder = new StringBuilder();
            while (!reader.Done) {
                string line = reader.ReadLine();

                if (ParserHelpers.IsSingleLineFeed(line)) {
                    break;
                }
                messageBuilder.Append(line);
            }

            string message = messageBuilder.ToString();
            return new ChangeSet(id, author, email, message, DateTimeOffset.ParseExact(date, "ddd MMM d HH:mm:ss yyyy zzz", CultureInfo.InvariantCulture));
        }

        private IEnumerable<FileStatus> ParseStatus(IStringReader reader) {
            while (!reader.Done) {
                reader.SkipWhitespace();
                var subReader = reader.ReadLine().AsReader();
                string status = subReader.ReadUntilWhitespace().Trim();
                string path = subReader.ReadLine().Trim();
                yield return new FileStatus(path, ConvertStatus(status));
            }
        }

        private ChangeType ConvertStatus(string status) {
            switch (status) {
                case "A":
                case "AM":
                    return ChangeType.Added;
                case "M":
                case "MM":
                    return ChangeType.Modified;
                case "D":
                    return ChangeType.Deleted;
                case "R":
                    return ChangeType.Renamed;
                case "??":
                    return ChangeType.Untracked;
                default:
                    break;
            }

            throw new InvalidOperationException("Unsupported status " + status);
        }

        private class DiffRange {
            public int LeftFrom { get; set; }
            public int LeftTo { get; set; }
            public int RightFrom { get; set; }
            public int RightTo { get; set; }

            public static DiffRange Parse(IStringReader reader) {
                var range = new DiffRange();
                reader.Skip("@@");
                reader.SkipWhitespace();
                reader.Skip('-');
                range.LeftFrom = reader.ReadInt();
                reader.Skip(',');
                range.LeftTo = range.LeftFrom + reader.ReadInt();
                reader.SkipWhitespace();
                reader.Skip('+');
                range.RightFrom = reader.ReadInt();
                if (reader.Skip(',')) {
                    range.RightTo = range.RightFrom + reader.ReadInt();
                }
                else {
                    range.RightTo = range.RightFrom;
                }
                reader.SkipWhitespace();
                reader.Skip("@@");
                return range;
            }
        }
    }
}
