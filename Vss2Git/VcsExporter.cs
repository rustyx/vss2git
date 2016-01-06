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
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Hpdi.VssLogicalLib;
using System.Linq;

namespace Hpdi.Vss2Git
{
    /// <summary>
    /// Replays and commits changesets into a new git or svn repository.
    /// </summary>
    /// <author>Trevor Robinson</author>
    class VcsExporter : Worker
    {
        private const string DefaultComment = "";

        private readonly VssDatabase database;
        private readonly RevisionAnalyzer revisionAnalyzer;
        private readonly ChangesetBuilder changesetBuilder;
        private readonly IVcsWrapper vcsWrapper;
        private readonly IDictionary<string, string> usernameDictionary;
        private readonly IDictionary<string, string> emailDictionary;
        private readonly StreamCopier streamCopier = new StreamCopier();
        private readonly HashSet<string> tagsUsed = new HashSet<string>();

        private string emailDomain = "";
        public string EmailDomain
        {
            get { return emailDomain; }
            set { emailDomain = value; }
        }

        private bool resetRepo = true;
        public bool ResetRepo
        {
            get { return resetRepo; }
            set { resetRepo = value; }
        }

        private Encoding commitEncoding = Encoding.UTF8;
        private DateTime? continueAfter;

        public Encoding CommitEncoding
        {
            get { return commitEncoding; }
            set { commitEncoding = value; }
        }

        public VcsExporter(WorkQueue workQueue, Logger logger,
            RevisionAnalyzer revisionAnalyzer, ChangesetBuilder changesetBuilder,
            IVcsWrapper vcsWrapper, IDictionary<string, string> usersmap)
            : base(workQueue, logger)
        {
            this.database = revisionAnalyzer.Database;
            this.revisionAnalyzer = revisionAnalyzer;
            this.changesetBuilder = changesetBuilder;
            this.vcsWrapper = vcsWrapper;
            this.emailDictionary = new Dictionary<string, string>();
            this.usernameDictionary = new Dictionary<string, string>();
            Regex emailRegex = new Regex("(\\S.*?)\\s*<(.+)>");
            foreach (var e in usersmap)
            {
                string key = e.Key.ToLower();
                string email = e.Value.Trim();
                if (email == "")
                    continue;
                Match m = emailRegex.Match(email);
                if (m.Success)
                {
                    this.usernameDictionary.Add(key, m.Groups[1].Value);
                    email = m.Groups[2].Value;
                }
                this.emailDictionary.Add(key, email);
            }
        }

        public void ExportToVcs(string repoPath, DateTime? continueAfter)
        {
            this.continueAfter = continueAfter;
            workQueue.AddLast(delegate (object work)
            {
                var stopwatch = Stopwatch.StartNew();

                logger.WriteSectionSeparator();
                LogStatus(work, "Initializing repository");

                if (continueAfter != null && resetRepo)
                    throw new ProcessException("Unable to continue sync and reset repo at the same time!", null, null);
                // create repository directory if it does not exist
                if (!Directory.Exists(repoPath))
                {
                    if (continueAfter != null)
                        throw new ProcessException("Unable to continue: " + repoPath + " does not exist.", null, null);
                    Directory.CreateDirectory(repoPath);
                }

                string vcs = vcsWrapper.GetVcs();

                if (vcs != null)
                {
                    while (!vcsWrapper.FindExecutable())
                    {
                        var button = MessageBox.Show(vcs + " not found in PATH. " +
                                                     "If you need to modify your PATH variable, please " +
                                                     "restart the program for the changes to take effect.",
                            "Error", MessageBoxButtons.RetryCancel, MessageBoxIcon.Error);
                        if (button == DialogResult.Cancel)
                        {
                            workQueue.Abort();
                            return;
                        }
                    }
                }

                if (!RetryCancel(delegate { vcsWrapper.Init(resetRepo); }))
                {
                    return;
                }

                AbortRetryIgnore(delegate
                {
                    vcsWrapper.Configure(continueAfter == null);
                });

                var pathMapper = new VssPathMapper();

                // create mappings for root projects
                foreach (var rootProject in revisionAnalyzer.RootProjects)
                {
                    // root must be repo path here - the path mapper uses paths relative to this one
                    var rootPath = repoPath;
                    pathMapper.SetProjectPath(rootProject.PhysicalName, rootPath, rootProject.Path);
                }

                var changesets = changesetBuilder.Changesets;

                // create a log of all MoveFrom and MoveTo actions
                logger.WriteSectionSeparator();
                logger.WriteLine("List of Move Operations");
                logger.WriteSectionSeparator();

                int changeSetNo = 0;
                int revisionNo = 0;
                foreach (var changeset in changesets)
                {
                    foreach (var revision in changeset.Revisions)
                    {
                        var actionType = revision.Action.Type;
                        VssItemName target = null;
                        var namedAction = revision.Action as VssNamedAction;
                        if (namedAction != null)
                        {
                            target = namedAction.Name;
                        }

                        switch (actionType)
                        {
                            case VssActionType.MoveFrom:
                                var moveFromAction = (VssMoveFromAction)revision.Action;
                                logger.WriteLine("{3}-{4}-{0}: MoveFrom {1} to {2}",
                                    revision.Item, moveFromAction.OriginalProject, target, changeSetNo, revisionNo);
                                break;
                            case VssActionType.MoveTo:
                                var moveToAction = (VssMoveToAction)revision.Action;
                                logger.WriteLine("{3}-{4}-{0}: MoveTo {1} from {2}",
                                    revision.Item, moveToAction.NewProject, target, changeSetNo, revisionNo);
                                break;
                        }
                        revisionNo++;
                    }
                    changeSetNo++;
                }

                // replay each changeset
                logger.WriteSectionSeparator();
                logger.WriteLine("Replaying Changesets");

                var changesetId = 0;
                var commitCount = 0;
                var tagCount = 0;
                var replayStopwatch = new Stopwatch();
                var labels = new LinkedList<Revision>();
                tagsUsed.Clear();

                bool found = continueAfter == null;

                foreach (var changeset in changesets)
                {
                    ++changesetId;
                    if (workQueue.IsAborting)
                    {
                        return;
                    }

                    if (!found)
                    {
                        if (continueAfter.Equals(changeset.DateTime))
                            found = true;
                        SkipChangeset(pathMapper, changeset);
                        continue;
                    }

                    var changesetDesc = string.Format(CultureInfo.InvariantCulture,
                        "changeset {0} from {1}", changesetId, changeset.DateTime);

                    // replay each revision in changeset
                    logger.WriteSectionSeparator();
                    LogStatus(work, "Replaying " + changesetDesc);
                    labels.Clear();
                    replayStopwatch.Start();
                    try
                    {
                        ReplayChangeset(pathMapper, changeset, labels);
                        vcsWrapper.NeedsCommit(); // to flush outstanding adds/deletes
                    }
                    finally
                    {
                        replayStopwatch.Stop();
                    }

                    if (workQueue.IsAborting)
                    {
                        return;
                    }

                    // commit changes
                    if (vcsWrapper.NeedsCommit())
                    {
                        LogStatus(work, "Committing " + changesetDesc);
                        if (CommitChangeset(changeset))
                        {
                            ++commitCount;
                        }
                    }

                    // create tags for any labels in the changeset
                    if (labels.Count > 0)
                    {
                        foreach (Revision label in labels)
                        {
                            var labelName = ((VssLabelAction)label.Action).Label;
                            if (string.IsNullOrEmpty(labelName))
                            {
                                logger.WriteLine("NOTE: Ignoring empty label");
                            }
                            else if (commitCount == 0)
                            {
                                logger.WriteLine("NOTE: Ignoring label '{0}' before initial commit", labelName);
                            }
                            else
                            {
                                var tagName = GetTagFromLabel(labelName);

                                var tagMessage = "Creating tag " + tagName;
                                if (tagName != labelName)
                                {
                                    tagMessage += " for label '" + labelName + "'";
                                }
                                LogStatus(work, tagMessage);

                                // tags always get a tag message;
                                var tagComment = label.Comment;
                                if (string.IsNullOrEmpty(tagComment))
                                {
                                    // use the original VSS label as the tag message if none was provided
                                    tagComment = labelName;
                                }

                                if (AbortRetryIgnore(
                                    delegate
                                    {
                                        vcsWrapper.Tag(tagName, label.User, GetEmail(label.User),
                                            tagComment, label.DateTime);
                                    }))
                                {
                                    ++tagCount;
                                }
                            }
                        }
                    }
                }

                stopwatch.Stop();

                if (!found)
                {
                    logger.WriteLine("Cannot Sync: VSS changeset at {0} not found.", continueAfter);
                    throw new ProcessException(string.Format("Cannot Sync: VSS changeset at {0} not found.", continueAfter), null, null);
                }

                logger.WriteSectionSeparator();
                logger.WriteLine(vcs + " export complete in {0:HH:mm:ss}", new DateTime(stopwatch.ElapsedTicks));
                logger.WriteLine("Replay time: {0:HH:mm:ss}", new DateTime(replayStopwatch.ElapsedTicks));
                logger.WriteLine(vcs + " time: {0:HH:mm:ss}", new DateTime(vcsWrapper.ElapsedTime().Ticks));
                logger.WriteLine(vcs + " commits: {0}", commitCount);
                logger.WriteLine(vcs + " tags: {0}", tagCount);
            });
        }

        private void ReplayChangeset(VssPathMapper pathMapper, Changeset changeset,
            LinkedList<Revision> labels)
        {
            foreach (Revision revision in changeset.Revisions)
            {
                if (workQueue.IsAborting)
                {
                    break;
                }

                AbortRetryIgnore(delegate
                {
                    ReplayRevision(pathMapper, revision, labels);
                });
            }
        }

        private void ReplayRevision(VssPathMapper pathMapper, Revision revision,
            LinkedList<Revision> labels)
        {
            var actionType = revision.Action.Type;
            if (revision.Item.IsProject)
            {
                // note that project path (and therefore target path) can be
                // null if a project was moved and its original location was
                // subsequently destroyed
                var project = revision.Item;
                var projectName = project.LogicalName;
                var projectPath = pathMapper.GetProjectPath(project.PhysicalName);
                var projectDesc = projectPath;
                if (projectPath == null)
                {
                    projectDesc = revision.Item.ToString();
                    logger.WriteLine("NOTE: {0} is currently unmapped", project);
                }

                VssItemName target = null;
                string targetPath = null;
                var namedAction = revision.Action as VssNamedAction;
                if (namedAction != null)
                {
                    target = namedAction.Name;
                    if (projectPath != null)
                    {
                        targetPath = Path.Combine(projectPath, target.LogicalName);
                    }
                }

                bool isAddAction = false;
                bool writeProject = false;
                bool writeFile = false;
                VssItemInfo itemInfo = null;
                switch (actionType)
                {
                    case VssActionType.Label:
                        // defer tagging until after commit
                        labels.AddLast(revision);
                        break;

                    case VssActionType.Create:
                        // ignored; items are actually created when added to a project
                        break;

                    case VssActionType.Add:
                    case VssActionType.Share:
                        logger.WriteLine("{0}: {1} {2}", projectDesc, actionType, target.LogicalName);
                        itemInfo = pathMapper.AddItem(project, target);
                        isAddAction = true;
                        break;

                    case VssActionType.Recover:
                        logger.WriteLine("{0}: {1} {2}", projectDesc, actionType, target.LogicalName);
                        itemInfo = pathMapper.RecoverItem(project, target);
                        isAddAction = true;
                        break;

                    case VssActionType.Delete:
                    case VssActionType.Destroy:
                        {
                            logger.WriteLine("{0}: {1} {2}", projectDesc, actionType, target.LogicalName);
                            itemInfo = pathMapper.DeleteItem(project, target);
                            if (targetPath != null && !itemInfo.Destroyed)
                            {
                                if (target.IsProject)
                                {
                                    if (Directory.Exists(targetPath))
                                    {
                                        if (((VssProjectInfo)itemInfo).ContainsFiles())
                                        {
                                            vcsWrapper.RemoveDir(targetPath, true);
                                        }
                                        else
                                        {
                                            vcsWrapper.RemoveEmptyDir(targetPath);
                                        }
                                    }
                                }
                                else
                                {
                                    if (File.Exists(targetPath))
                                    {
                                        // not sure how it can happen, but a project can evidently
                                        // contain another file with the same logical name, so check
                                        // that this is not the case before deleting the file
                                        if (pathMapper.ProjectContainsLogicalName(project, target))
                                        {
                                            logger.WriteLine("NOTE: {0} contains another file named {1}; not deleting file",
                                                projectDesc, target.LogicalName);
                                        }
                                        else
                                        {
                                            vcsWrapper.RemoveFile(targetPath);
                                        }
                                    }
                                }
                            }
                        }
                        break;

                    case VssActionType.Rename:
                        {
                            var renameAction = (VssRenameAction)revision.Action;
                            logger.WriteLine("{0}: {1} {2} to {3}",
                                projectDesc, actionType, renameAction.OriginalName, target.LogicalName);
                            itemInfo = pathMapper.RenameItem(target);
                            if (targetPath != null && !itemInfo.Destroyed)
                            {
                                var sourcePath = Path.Combine(projectPath, renameAction.OriginalName);
                                if (target.IsProject ? Directory.Exists(sourcePath) : File.Exists(sourcePath))
                                {
                                    // renaming a file or a project that contains files?
                                    var projectInfo = itemInfo as VssProjectInfo;
                                    if (projectInfo == null || projectInfo.ContainsFiles())
                                    {
                                        CaseSensitiveRename(sourcePath, targetPath, vcsWrapper.Move);
                                    }
                                    else
                                    {
                                        CaseSensitiveRename(sourcePath, targetPath, vcsWrapper.MoveEmptyDir);
                                    }
                                }
                                else
                                {
                                    logger.WriteLine("NOTE: Skipping rename because {0} does not exist", sourcePath);
                                }
                            }
                        }
                        break;

                    case VssActionType.MoveFrom:
                        // if both MoveFrom & MoveTo are present (e.g.
                        // one of them has not been destroyed), only one
                        // can succeed, so check that the source exists
                        {
                            var moveFromAction = (VssMoveFromAction)revision.Action;
                            logger.WriteLine("{0}: Move from {1} to {2}",
                                projectDesc, moveFromAction.OriginalProject, targetPath ?? target.LogicalName);

                            var isInside = pathMapper.IsInRoot(moveFromAction.OriginalProject);

                            if (isInside)
                            {
                                // MoveFrom -> inside scope: handle actual move in VCS
                                logger.WriteLine("start MoveFrom -> inside " + moveFromAction.OriginalProject + " (move in VCS)");

                                var sourcePath = pathMapper.GetProjectPath(target.PhysicalName);
                                if (sourcePath != null && sourcePath.Equals(targetPath))
                                {
                                    logger.WriteLine("***** warning: move with source path equal to target path " + sourcePath);
                                }
                                itemInfo = pathMapper.MoveProjectFrom(
                                    project, target, moveFromAction.OriginalProject);

                                if (sourcePath != null && Directory.Exists(sourcePath))
                                {
                                    if (((VssProjectInfo)itemInfo).ContainsFiles())
                                    {
                                        vcsWrapper.Move(sourcePath, targetPath);
                                    }
                                    else
                                    {
                                        vcsWrapper.MoveEmptyDir(sourcePath, targetPath);
                                    }
                                }
                                else
                                {
                                    logger.WriteLine("***** warning: inside move with non existing source " + moveFromAction.OriginalProject);
                                }
                            }
                            else
                            {
                                // MoveFrom -> outside scope: recover
                                logger.WriteLine("start MoveFrom -> outside " + moveFromAction.OriginalProject + " (recover)");
                                itemInfo = pathMapper.RecoverItem(project, target);
                                isAddAction = true;
                            }
                        }
                        break;

                    case VssActionType.MoveTo:
                        {
                            // handle actual moves in MoveFrom; this just does cleanup of destroyed projects
                            var moveToAction = (VssMoveToAction)revision.Action;
                            logger.WriteLine("{0}: Move to {1} from {2}",
                                projectDesc, moveToAction.NewProject, targetPath ?? target.LogicalName);

                            var isInside = pathMapper.IsInRoot(moveToAction.NewProject);
                            if (isInside)
                            {
                                // MoveTo -> inside scope: do nothing - paired with corresponding MoveFrom that handles actual move
                                logger.WriteLine("start MoveTo -> inside " + moveToAction.NewProject + " (do nothing)");
                            }
                            else
                            {
                                // MoveTo -> outside scope: delete - no matching MoveFrom available
                                logger.WriteLine("start MoveTo -> outside " + moveToAction.NewProject + " (delete)");

                                itemInfo = pathMapper.DeleteItem(project, target);
                                if (targetPath != null && target.IsProject)
                                {
                                    logger.WriteLine("MoveTo delete");
                                    // project was moved to a now-destroyed project; remove the directory
                                    if (((VssProjectInfo)itemInfo).ContainsFiles())
                                    {
                                        vcsWrapper.RemoveDir(targetPath, true);
                                    }
                                    else
                                    {
                                        vcsWrapper.RemoveEmptyDir(targetPath);
                                    }
                                    if (Directory.Exists(targetPath))
                                    {
                                        Directory.Delete(targetPath, true);
                                    }
                                    logger.WriteLine("MoveTo delete (done)");
                                }
                            }
                        }
                        break;

                    case VssActionType.Pin:
                        {
                            var pinAction = (VssPinAction)revision.Action;
                            if (pinAction.Pinned)
                            {
                                logger.WriteLine("{0}: Pin {1}", projectDesc, target.LogicalName);
                                itemInfo = pathMapper.PinItem(project, target);
                            }
                            else
                            {
                                logger.WriteLine("{0}: Unpin {1}", projectDesc, target.LogicalName);
                                itemInfo = pathMapper.UnpinItem(project, target);
                                writeFile = !itemInfo.Destroyed;
                            }
                        }
                        break;

                    case VssActionType.Branch:
                        {
                            var branchAction = (VssBranchAction)revision.Action;
                            logger.WriteLine("{0}: {1} {2}", projectDesc, actionType, target.LogicalName);
                            itemInfo = pathMapper.BranchFile(project, target, branchAction.Source);
                            // branching within the project might happen after branching of the file
                            writeFile = true;
                        }
                        break;

                    case VssActionType.Archive:
                        // currently ignored
                        {
                            var archiveAction = (VssArchiveAction)revision.Action;
                            logger.WriteLine("{0}: Archive {1} to {2} (ignored)",
                                projectDesc, target.LogicalName, archiveAction.ArchivePath);
                        }
                        break;

                    case VssActionType.Restore:
                        {
                            var restoreAction = (VssRestoreAction)revision.Action;
                            logger.WriteLine("{0}: Restore {1} from archive {2}",
                                projectDesc, target.LogicalName, restoreAction.ArchivePath);
                            itemInfo = pathMapper.AddItem(project, target);
                            isAddAction = true;
                        }
                        break;
                }

                if (targetPath != null)
                {
                    if (isAddAction)
                    {
                        if (revisionAnalyzer.IsDestroyed(target.PhysicalName) &&
                            !database.ItemExists(target.PhysicalName))
                        {
                            logger.WriteLine("NOTE: Skipping destroyed file: {0}", targetPath);
                            itemInfo.Destroyed = true;
                        }
                        else if (target.IsProject)
                        {
                            writeProject = true;
                        }
                        else
                        {
                            writeFile = true;
                        }
                    }

                    if (writeProject && pathMapper.IsProjectRooted(target.PhysicalName))
                    {
                        Directory.CreateDirectory(targetPath);
                        // create all contained subdirectories
                        foreach (var projectInfo in pathMapper.GetAllProjects(target.PhysicalName))
                        {
                            logger.WriteLine("{0}: Creating subdirectory {1}",
                                projectDesc, projectInfo.LogicalName);
                            Directory.CreateDirectory(projectInfo.GetPath());
                        }

                        vcsWrapper.AddDir(targetPath);
                        // write current rev of all contained files
                        foreach (var fileInfo in pathMapper.GetAllFiles(target.PhysicalName))
                        {
                            WriteRevision(pathMapper, actionType, fileInfo.PhysicalName,
                                fileInfo.Version, target.PhysicalName);
                        }
                    }
                    else if (writeFile)
                    {
                        // write current rev to working path
                        int version = pathMapper.GetFileVersion(target.PhysicalName);
                        if (WriteRevisionTo(target.PhysicalName, version, targetPath))
                        {
                            // add file explicitly, so it is visible to subsequent vcs operations
                            vcsWrapper.Add(targetPath);
                        }
                    }
                }
            }
            // item is a file, not a project
            else if (actionType == VssActionType.Edit || actionType == VssActionType.Branch)
            {
                // if the action is Branch, the following code is necessary only if the item
                // was branched from a file that is not part of the migration subset; it will
                // make sure we start with the correct revision instead of the first revision

                var target = revision.Item;

                // update current rev
                pathMapper.SetFileVersion(target, revision.Version);

                // write current rev to all sharing projects
                WriteRevision(pathMapper, actionType, target.PhysicalName,
                    revision.Version, null);
                vcsWrapper.SetNeedsCommit();
            }
        }

        private void SkipChangeset(VssPathMapper pathMapper, Changeset changeset)
        {
            foreach (Revision revision in changeset.Revisions)
            {
                if (workQueue.IsAborting)
                {
                    break;
                }
                SkipRevision(pathMapper, revision);
            }
        }

        private void SkipRevision(VssPathMapper pathMapper, Revision revision)
        {
            var actionType = revision.Action.Type;
            if (revision.Item.IsProject)
            {
                var project = revision.Item;
                var projectPath = pathMapper.GetProjectPath(project.PhysicalName);

                VssItemName target = null;
                string targetPath = null;
                var namedAction = revision.Action as VssNamedAction;
                if (namedAction != null)
                {
                    target = namedAction.Name;
                    if (projectPath != null)
                    {
                        targetPath = Path.Combine(projectPath, target.LogicalName);
                    }
                }

                switch (actionType)
                {
                    case VssActionType.Add:
                    case VssActionType.Share:
                        pathMapper.AddItem(project, target);
                        break;

                    case VssActionType.Recover:
                        pathMapper.RecoverItem(project, target);
                        break;

                    case VssActionType.Delete:
                    case VssActionType.Destroy:
                        pathMapper.DeleteItem(project, target);
                        break;

                    case VssActionType.Rename:
                        pathMapper.RenameItem(target);
                        break;

                    case VssActionType.MoveFrom:
                        {
                            var moveFromAction = (VssMoveFromAction)revision.Action;
                            var isInside = pathMapper.IsInRoot(moveFromAction.OriginalProject);

                            if (isInside)
                            {
                                pathMapper.MoveProjectFrom(project, target, moveFromAction.OriginalProject);
                            }
                            else
                            {
                                pathMapper.RecoverItem(project, target);
                            }
                        }
                        break;

                    case VssActionType.MoveTo:
                        {
                            var moveToAction = (VssMoveToAction)revision.Action;
                            var isInside = pathMapper.IsInRoot(moveToAction.NewProject);
                            if (!isInside)
                            {
                                pathMapper.DeleteItem(project, target);
                            }
                        }
                        break;

                    case VssActionType.Pin:
                        {
                            var pinAction = (VssPinAction)revision.Action;
                            if (pinAction.Pinned)
                            {
                                pathMapper.PinItem(project, target);
                            }
                            else
                            {
                                pathMapper.UnpinItem(project, target);
                            }
                        }
                        break;

                    case VssActionType.Branch:
                        {
                            var branchAction = (VssBranchAction)revision.Action;
                            pathMapper.BranchFile(project, target, branchAction.Source);
                        }
                        break;

                    case VssActionType.Restore:
                        pathMapper.AddItem(project, target);
                        break;
                }
            }
            // item is a file, not a project
            else if (actionType == VssActionType.Edit || actionType == VssActionType.Branch)
            {
                pathMapper.SetFileVersion(revision.Item, revision.Version);
            }
        }

        private bool CommitChangeset(Changeset changeset)
        {
            var result = false;
            AbortRetryIgnore(delegate
            {
                result = vcsWrapper.AddAll() &&
                    vcsWrapper.Commit(GetUsername(changeset.User), GetEmail(changeset.User),
                    changeset.Comment ?? DefaultComment, changeset.DateTime);
            });
            return result;
        }

        private bool RetryCancel(ThreadStart work)
        {
            return AbortRetryIgnore(work, MessageBoxButtons.RetryCancel);
        }

        private bool AbortRetryIgnore(ThreadStart work)
        {
            return AbortRetryIgnore(work, MessageBoxButtons.AbortRetryIgnore);
        }

        private bool AbortRetryIgnore(ThreadStart work, MessageBoxButtons buttons)
        {
            bool retry;
            do
            {
                try
                {
                    work();
                    return true;
                }
                catch (Exception e)
                {
                    var message = LogException(e);

                    message += "\nSee log file for more information.";

                    var button = MessageBox.Show(message, "Error", buttons, MessageBoxIcon.Error);
                    switch (button)
                    {
                        case DialogResult.Retry:
                            retry = true;
                            break;
                        case DialogResult.Ignore:
                            retry = false;
                            break;
                        default:
                            retry = false;
                            workQueue.Abort();
                            break;
                    }
                }
            } while (retry);
            return false;
        }

        private string GetUsername(string user)
        {
            // keys to the dictionary: user name in lower case, blanks replaced by dots
            user = user.ToLower();
            if (usernameDictionary != null && usernameDictionary.ContainsKey(user))
            {
                return usernameDictionary[user];
            }
            // if we can't find the user in the dictionary, we use the user name as-is
            return user;
        }

        private string GetEmail(string user)
        {
            // keys to the dictionary: user name in lower case, blanks replaced by dots
            user = user.ToLower();
            if (emailDictionary != null && emailDictionary.ContainsKey(user))
            {
                return emailDictionary[user];
            }
            // if we can't find the user in the dictionary, we generate the email from the username
            return emailDomain.Equals("") ? user : user.Replace(' ', '.') + "@" + emailDomain;
        }

        private string GetTagFromLabel(string label)
        {
            // vcs tag names must be valid filenames, so replace sequences of
            // invalid characters with an underscore
            var baseTag = Regex.Replace(label, "[^A-Za-z0-9_-]+", "_");

            // vcs tags are global, whereas VSS tags are local, so ensure
            // global uniqueness by appending a number; since the file system
            // may be case-insensitive, ignore case when hashing tags
            var tag = baseTag;
            for (int i = 2; !tagsUsed.Add(tag.ToUpperInvariant()); ++i)
            {
                tag = baseTag + "-" + i;
            }

            return tag;
        }

        private void WriteRevision(VssPathMapper pathMapper, VssActionType actionType,
            string physicalName, int version, string underProject)
        {
            var paths = pathMapper.GetFilePaths(physicalName, underProject);
            if (!paths.Any())
            {
                logger.WriteLine("WARNING: {0}: no physical path, {1} revision {2} skipped", physicalName, actionType, version);
            }
            foreach (string path in paths)
            {
                logger.WriteLine("{0}: {1} revision {2}", path, actionType, version);
                if (WriteRevisionTo(physicalName, version, path))
                {
                    // add file explicitly, so it is visible to subsequent vcs operations
                    vcsWrapper.Add(path);
                }
            }
        }

        private bool WriteRevisionTo(string physical, int version, string destPath)
        {
            VssFile item;
            VssFileRevision revision;
            Stream contents;
            try
            {
                item = (VssFile)database.GetItemPhysical(physical);
                revision = item.GetRevision(version);
                contents = revision.GetContents();
            }
            catch (Exception e)
            {
                // log an error for missing data files or versions, but keep processing
                var message = ExceptionFormatter.Format(e);
                logger.WriteLine("ERROR: {0}", message);
                logger.WriteLine(e);
                return false;
            }

            // propagate exceptions here (e.g. disk full) to abort/retry/ignore
            using (contents)
            {
                if (!WriteStream(contents, destPath))
                    return false;
            }

            // try to use the first revision (for this branch) as the create time,
            // since the item creation time doesn't seem to be meaningful
            var createDateTime = item.Created;
            using (var revEnum = item.Revisions.GetEnumerator())
            {
                if (revEnum.MoveNext())
                {
                    createDateTime = revEnum.Current.DateTime;
                }
            }

            // set file creation and update timestamps
            File.SetCreationTimeUtc(destPath, TimeZoneInfo.ConvertTimeToUtc(createDateTime));
            File.SetLastWriteTimeUtc(destPath, TimeZoneInfo.ConvertTimeToUtc(revision.DateTime));

            return true;
        }

        private bool WriteStream(Stream inputStream, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            // check whether the new contents actually differs from what's on disk

            if (File.Exists(path) && inputStream.Length == new FileInfo(path).Length)
            {
                using (var outputStream = File.OpenRead(path))
                {
                    byte[] inBuffer = new byte[4096];
                    byte[] outBuffer = new byte[4096];

                    bool differs = false;

                    while (!differs)
                    {
                        int read = inputStream.Read(inBuffer, 0, inBuffer.Length);
                        if (read <= 0)
                            break;

                        outputStream.Read(outBuffer, 0, outBuffer.Length);

                        for (int i = 0; i < read; i++)
                        {
                            if (inBuffer[i] != outBuffer[i])
                            {
                                differs = true;
                                break;
                            }
                        }
                    }

                    if (!differs)
                         return false;

                    inputStream.Seek(0, SeekOrigin.Begin);
                }
            }

            using (var outputStream = new FileStream(
                path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                streamCopier.Copy(inputStream, outputStream);
            }

            return true;
        }

        private delegate void RenameDelegate(string sourcePath, string destPath);

        private void CaseSensitiveRename(string sourcePath, string destPath, RenameDelegate renamer)
        {
            if (sourcePath.Equals(destPath, StringComparison.OrdinalIgnoreCase))
            {
                // workaround for case-only renames on case-insensitive file systems:

                var sourceDir = Path.GetDirectoryName(sourcePath);
                var sourceFile = Path.GetFileName(sourcePath);
                var destDir = Path.GetDirectoryName(destPath);
                var destFile = Path.GetFileName(destPath);

                if (sourceDir != destDir)
                {
                    // recursively rename containing directories that differ in case
                    CaseSensitiveRename(sourceDir, destDir, renamer);

                    // fix up source path based on renamed directory
                    sourcePath = Path.Combine(destDir, sourceFile);
                }

                if (sourceFile != destFile)
                {
                    // use temporary filename to rename files that differ in case
                    var tempPath = sourcePath + ".mvtmp";
                    CaseSensitiveRename(sourcePath, tempPath, renamer);
                    CaseSensitiveRename(tempPath, destPath, renamer);
                }
            }
            else
            {
                renamer(sourcePath, destPath);
            }
        }
    }
}
