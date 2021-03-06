﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

using System.IO;
using Umbraco.Core;
using Umbraco.Core.IO;

using Umbraco.Core.Logging;

using Jumoo.uSync.Core.Extensions;

using Jumoo.uSync.BackOffice;
using Jumoo.uSync.BackOffice.Helpers;

using Jumoo.uSync.Migrations.Helpers;

using System.Xml.Linq;

namespace Jumoo.uSync.Migrations
{
    public class MigrationManager
    {
        private string _rootFolder;

        private List<string> _folders;

        public MigrationManager(string folder)
        {
            _rootFolder = IOHelper.MapPath(folder);

            if (!Directory.Exists(_rootFolder))
                Directory.CreateDirectory(_rootFolder);

            _folders = new List<string>();

            _folders.Add("views");
            _folders.Add("css");
            _folders.Add("app_code");
            _folders.Add("scripts");
            _folders.Add("xslt");
            _folders.Add("fonts");
        }

        public List<MigrationInfo> ListMigrations()
        {
            List<MigrationInfo> snapshots = new List<MigrationInfo>();
            if (Directory.Exists(_rootFolder))
            {
                foreach (var dir in Directory.GetDirectories(_rootFolder))
                {
                    DirectoryInfo snapshotDir = new DirectoryInfo(dir);

                    snapshots.Add(new MigrationInfo(dir));
                }
            }

            return snapshots;
        }

        public MigrationInfo CreateMigration(string name)
        {
            var masterSnap = CombineMigrations(_rootFolder);

            var snapshotFolder = Path.Combine(_rootFolder,
                string.Format("{0}_{1}", DateTime.Now.ToString("yyyyMMdd_HHmmss"), name.ToSafeFileName()));

            uSyncBackOfficeContext.Instance.ExportAll(snapshotFolder);

            LogHelper.Info<MigrationManager>("Export Complete");

            foreach (var folder in _folders)
            {
          
                var source = IOHelper.MapPath("~/" + folder);
                if (Directory.Exists(source))
                {
                    LogHelper.Info<MigrationManager>("Including {0} in snapshot", () => source);
                    var target = Path.Combine(snapshotFolder, folder);
                    MigrationIO.MergeFolder(target, source);
                }
            }

            LogHelper.Info<MigrationManager>("Extra folders copied");

            // now we delete anything that is in any of the previous snapshots.
            if (!string.IsNullOrEmpty(masterSnap))
            {

                // Capture deletes since last snapshot
                //   things in the master but not in our new one, must have 
                //   gone missing (delete?)

                IdentifyDeletes(masterSnap, snapshotFolder);

                // take anything that is now in our snapshotFolder and masterSnapshot
                // this will leave just the changes..
                MigrationIO.RemoveDuplicates(snapshotFolder, masterSnap);
                Directory.Delete(masterSnap, true);
            }

            LogHelper.Info<MigrationManager>("Cleaned Snapshot up..");

            if (!Directory.Exists(snapshotFolder))
            {
                // empty snapshot
                LogHelper.Info<MigrationManager>("No changes in this snapshot");
            }

            return new MigrationInfo(snapshotFolder);
        }

        /// <summary>
        ///  takes everything in the snapshot folder, builds a master snapshot
        ///  and then runs it through an import
        /// </summary>
        public IEnumerable<uSyncAction> ApplyMigrations()
        {
            var snapshotImport = CombineMigrations(_rootFolder);

            if (Directory.Exists(snapshotImport))
            {
                var actions = uSyncBackOfficeContext.Instance.ImportAll(snapshotImport);

                // Import the other folders across to umbraco...
                foreach(var folder in _folders)
                {
                    var snapshotFolder = Path.Combine(snapshotImport, folder);
                    var target = IOHelper.MapPath("~/" + folder);

                    // copy across.
                    MigrationIO.MergeFolder(target, snapshotFolder);
                }

                return actions;
            }

            return null;
        }

        #region Snapshot Creation
        /// <summary>
        ///  builds a master snapshot, of all existing
        ///  snapshots, this can then be used as the import folder
        ///  meaning we import just once. 
        /// 
        ///  also good when creating snapshots, we only create stuff
        ///  that is not in our existing snapshot folders.
        /// </summary>
        /// <param name="snapshotFolder"></param>
        /// <returns></returns>
        private string CombineMigrations(string snapshotFolder)
        {
            var tempRoot = IOHelper.MapPath(Path.Combine(SystemDirectories.Data, "temp", "usync", "migrations"));

           if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, true);

            Directory.CreateDirectory(tempRoot);

            DirectoryInfo root = new DirectoryInfo(snapshotFolder);

            var snapshots = root.GetDirectories().OrderBy(x => x.Name);

            if (snapshots.Any())
            {
                foreach (var snapshot in snapshots)
                {
                    MigrationIO.MergeFolder(tempRoot, snapshot.FullName);
                }
            }

            return tempRoot;
        }

        /// <summary>
        ///  workout what is a delete, anything that isn't in the target but is in the master
        ///  should be a delete.
        /// </summary>
        /// <param name="master"></param>
        /// <param name="target"></param>
        /// <returns></returns>
        private void IdentifyDeletes(string master, string target)
        {
            var missingFiles = MigrationIO.LeftOnlyFiles(master, target);

            var actionTracker = new ActionTracker(target);

            foreach(var file in missingFiles)
            {
                // work out the type of file...
                if (File.Exists(file.FullName))
                {
                    XElement node = XElement.Load(file.FullName);
                    var itemType = node.GetUmbracoType();
                    var key = node.NameFromNode();

                    // we need to find id's to handle deletes,
                    // and we need to check that the thing hasn't been renamed. 
                    // so if it exsits only in master we need to double check its id 
                    // doesn't still exist somewhere else on the install with the 
                    // same id but a different name, 

                    // we basically need some id hunting shizzel. 
                    if (itemType != default(Type))
                    {
                        var fileKey = MigrationIDHunter.GetItemId(node);
                        if (!string.IsNullOrEmpty(fileKey))
                        {
                            if (MigrationIDHunter.FindInFiles(target, fileKey))
                            {
                                // the key exists somewhere else in the 
                                // folder, so it's a rename of something
                                // we won't add this one to the delete pile.

                                // but we will need to signal somehow that 
                                // the old name is wrong, so that on a full
                                // merge the two files don't get included.
                                actionTracker.AddAction(SyncActionType.Obsolete, file.FullName, itemType);

                                continue;
                            }
                        }
                    }

                    // if we can't workout what type of thing it is, we assume
                    // it's a file, then we can deal with it like a delete 
                    // later on.
                    if (itemType == default(Type))
                        itemType = typeof(FileInfo);

                    actionTracker.AddAction(SyncActionType.Delete, key, itemType);
                }

            }

            actionTracker.SaveActions();
        }
        
        #endregion
    }
}
