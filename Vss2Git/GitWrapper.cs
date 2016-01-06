/* Copyright 2009 HPDI, LLC
 * 
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 * 
 *     http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using NGit;
using NGit.Api;
using NGit.Dircache;

namespace Hpdi.Vss2Git
{
    /// <summary>
    /// Wraps execution of Git and implements the common Git commands.
    /// </summary>
    /// <author>Trevor Robinson</author>
    class GitWrapper : AbstractVcsWrapper
    {
        public static readonly string gitMetaDir = ".git";

        private List<String> addQueue = new List<string>();
        private List<String> deleteQueue = new List<string>();
        private List<String> dirDeleteQueue = new List<string>();
        private Git git;

        private Encoding commitEncoding = Encoding.UTF8;

        public Encoding CommitEncoding
        {
            get { return commitEncoding; }
            set { commitEncoding = value; }
        }

        private bool forceAnnotatedTags = true;
        public bool ForceAnnotatedTags
        {
            get { return forceAnnotatedTags; }
            set { forceAnnotatedTags = value; }
        }

        public GitWrapper(string outputDirectory, Logger logger, Encoding commitEncoding,
            bool forceAnnotatedTags)
            : base(outputDirectory, logger, null, gitMetaDir)
        {
            this.commitEncoding = commitEncoding;
            this.forceAnnotatedTags = forceAnnotatedTags;
        }

        public override string RelativePath(string path)
        {
            return base.RelativePath(path).Replace('\\', '/'); // cygwin git compatibility
        }

        public override void Init(bool resetRepo)
        {
            if (resetRepo)
            {
                DeleteDirectory(GetOutputDirectory());
                Thread.Sleep(0);
                Directory.CreateDirectory(GetOutputDirectory());
                git = Git.Init().SetDirectory(GetOutputDirectory()).Call();
            }
        }

        public override void Configure(bool newRepo)
        {
            if (commitEncoding.WebName != "utf-8")
            {
                SetConfig("i18n.commitencoding", commitEncoding.WebName);
            }
            CheckOutputDirectory(newRepo);
        }

        public override bool Add(string path)
        {
            addQueue.Add(path);
            return true;
        }

        private bool DoAdds()
        {
            if (addQueue.Count == 0)
                return false;

            var add = git.Add();
            foreach (string path in addQueue)
            {
                add.AddFilepattern(RelativePath(path));
            }
            addQueue.Clear();
            add.Call();
            base.SetNeedsCommit();
            return true;
        }

        public override bool AddDir(string path)
        {
            // do nothing - git does not care about directories
            return true;
        }
        public override bool NeedsCommit()
        {
            DoAdds();
            DoDeletes();
            return base.NeedsCommit();
        }

        public override bool AddAll()
        {
            // git.Add().AddFilepattern(".").Call();
            // base.SetNeedsCommit();
            return true;
        }

        public override void RemoveFile(string path)
        {
            deleteQueue.Add(path);
        }

        private bool DoDeletes()
        {
            if (deleteQueue.Count == 0)
                return false;

            var delete = git.Rm();
            foreach (string path in deleteQueue)
            {
                delete.AddFilepattern(RelativePath(path));
            }
            deleteQueue.Clear();
            delete.Call();
            CleanupEmptyDirs();
            base.SetNeedsCommit();
            return true;
        }

        private void CleanupEmptyDirs()
        {
            foreach (string dir in dirDeleteQueue)
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
            dirDeleteQueue.Clear();
        }

        public override void RemoveDir(string path, bool recursive)
        {
            deleteQueue.Add(path); // is always recursive
            dirDeleteQueue.Add(path);
        }

        public override void RemoveEmptyDir(string path)
        {
            // do nothing - remove only on file system - git doesn't care about directories with no files
        }

        public override void Move(string sourcePath, string destPath)
        {
            git.Rm().AddFilepattern(RelativePath(sourcePath)).Call();
            git.Add().AddFilepattern(RelativePath(destPath)).Call();
            base.SetNeedsCommit();
        }

        public override void MoveEmptyDir(string sourcePath, string destPath)
        {
            // move only on file system - git doesn't care about directories with no files
            Directory.Move(sourcePath, destPath);
        }

        public override void SetNeedsCommit()
        {
            // Suppress explicit calls.
        }

        public override bool DoCommit(string authorName, string authorEmail, string comment, DateTime localTime)
        {
#if false
            // enable this when you find empty commits or uncommitted changes; this will throw on that commit

            var status = git.Status().Call();

            if (status.IsClean())
                throw new InvalidOperationException("Expected changes");

            if (status.GetModified().Count > 0 || status.GetMissing().Count > 0 || status.GetUntracked().Count > 0 || status.GetConflicting().Count > 0)
                throw new InvalidOperationException("Have modified, missing, untracked or conflicting files");
#endif

            var person = new PersonIdent(authorName, authorEmail, localTime, TimeZoneInfo.Local);

            git.Commit()
                .SetMessage(comment)
                .SetAuthor(person)
                .SetCommitter(person)
                .Call();

            return true;
        }

        public override void Tag(string name, string taggerName, string taggerEmail, string comment, DateTime localTime)
        {
            git.Tag()
                .SetMessage(comment)
                .SetTagger(new PersonIdent(taggerName, taggerEmail, localTime, TimeZoneInfo.Local))
                .SetName(name)
                .Call();
        }

        private void SetConfig(string name, string value)
        {
            int pos = name.IndexOf('.');
            string section = name.Substring(0, pos);
            name = name.Substring(pos + 1);
            git.GetRepository().GetConfig().SetString(section, null, name, value);
        }

        public override DateTime? GetLastCommit()
        {
            if (git == null)
                return null;

            foreach (var commit in git.Log().SetMaxCount(1).Call())
            {
                long unixTimeStamp = commit.CommitTime;
                DateTime dt = new DateTime(1970, 1, 1, 0, 0, 0, 0, System.DateTimeKind.Utc);
                return dt.AddSeconds(unixTimeStamp).ToLocalTime();
            }

            return null;
        }

    }
}
