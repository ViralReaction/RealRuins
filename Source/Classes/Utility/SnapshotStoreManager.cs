﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

using Verse;

using System.IO;
using System.Runtime.Remoting.Messaging;
using System.Security.AccessControl;
using System.Security.Principal;


//Snapshot manager operates with blueprint identifiers, which do not have .bp extensions. Only SnapshotStoreManager (this class) is aware of extensions
//So you should not use .bp extension explicitly outside of this class.

namespace RealRuins
{
    class SnapshotStoreManager
    {

        private static SnapshotStoreManager instance = null;
        public static SnapshotStoreManager Instance {
            get {
                if (instance == null) {
                    instance = new SnapshotStoreManager();
                }
                return instance;
            }
        }

        private static string oldRootFolder = "../Snapshots";
        private long totalFilesSize = 0;
        private int totalFileCount = 0;


        public static bool HasPlanetaryBlueprintForCurrentGame(string blueprintName) {
            string gameName = CurrentGamePath();
            bool result = SnapshotExists(blueprintName, gameName);
            if (!result) {
                Debug.Log("[PRELOAD CHECKER]", "{0} does not exist at {1}, scheduling load.", blueprintName, gameName);
            }
            return result;
            
        }

        public static string GamePath(string seed, int mapSize, float coverage) {
            return string.Format("{0}-{1}-{2}", seed.SanitizeForFileSystem(), mapSize, (int)(coverage * 100));
        }

        public static string CurrentGamePath() {
            return GamePath(Find.World.info.seedString, Find.World.info.initialMapSize.x, Find.World.PlanetCoverage);
        }

        public SnapshotStoreManager() {
            MoveFilesIfNeeded();
            GetSnapshotsFolderPath();
            RecalculateFilesSize();
        }

        //moving data files to a new folder
        private void MoveFilesIfNeeded() {

            if (!Directory.Exists(oldRootFolder)) return;
            string newFolder = GetSnapshotsFolderPath();

            if (newFolder == oldRootFolder) return; //can't create new folder on some reason. fallback.

            string[] oldFiles = Directory.GetFiles(oldRootFolder);
            
            DateTime startTime = DateTime.Now;
            Debug.SysLog("Started moving {0} files at {1}", oldFiles.Length, startTime);

            foreach (string fullPath in oldFiles) {
                string filename = Path.GetFileName(fullPath);
                string newPath = Path.Combine(newFolder, filename);

                try {
                    if (!File.Exists(newPath)) {
                        File.Move(fullPath, newPath);
                    } else {
                        File.Delete(fullPath);
                    }
                } catch {
                    //actually ignore: can't do anything
                }
            }

            Debug.SysLog("finished at {0} ({1} msec)", DateTime.Now, (DateTime.Now - startTime).TotalMilliseconds);

            try {
                Directory.Delete(oldRootFolder);
            } catch {
                //m-kay
            }
        }
        
        private static string snapshotsFolderPath = null;

        private static string GetSnapshotsFolderPath() {
            if (snapshotsFolderPath == null) {
                snapshotsFolderPath = Path.Combine(GenFilePaths.SaveDataFolderPath, "RealRuins");
                DirectoryInfo directoryInfo = new DirectoryInfo(snapshotsFolderPath);
                if (!directoryInfo.Exists) {
                    try {
                        directoryInfo.Create();
                    }
                    catch {
                        snapshotsFolderPath = oldRootFolder;
                    }
                }
            }
            return snapshotsFolderPath;
        }
    
        public void StoreData(string buffer, string blueprintName) {
            StoreBinaryData(Encoding.UTF8.GetBytes(buffer), blueprintName);
        }

        public void StoreBinaryData(byte[] buffer, string blueprintName, string gameName = null) {

            new Thread(() => {
                string filename = blueprintName + ".bp";
                string path = GetSnapshotsFolderPath();
                if (gameName != null) {
                    path = Path.Combine(path, gameName);
                }
                if (RealRuins.SingleFile) {
                    filename = "jeluder.bp";
                }

                DirectoryInfo directoryInfo = new DirectoryInfo(path);
                if (!directoryInfo.Exists) {
                    try {
                        directoryInfo.Create();
                    } catch {
                        Debug.Error(Debug.Store, "Can't access store path");
                    }
                }

                //when storing a file you need to remove older version of snapshots of the same game
                string[] parts = filename.Split('=');
                if (parts.Count() > 1) {
                    int date = 0;
                    if (int.TryParse(parts[0], out date)) {
                        parts[0] = "*";
                        string mask = string.Join("=", parts);
                        string[] files = Directory.GetFiles(path, mask);
                        foreach (string existingFile in files) {
                            int existingFileDate = 0;
                            string[] existingFileParts = existingFile.Split('-');
                            if (int.TryParse(existingFileParts[0], out existingFileDate)) {
                                if (existingFileDate > date) {
                                    //there is more fresh file. no need to save this one.
                                    return;
                                } else {
                                    //remove older files
                                    File.Delete(existingFile);
                                }
                            }
                        }
                    }
                }
                //writing file in all cases except "newer version available"
                File.WriteAllBytes(Path.Combine(path, filename), buffer);
                RecalculateFilesSize();

            }).Start();

        }

        private string DoGetRandomFilenameFromRootFolder() {
            if (RealRuins.SingleFile) {
                return RealRuins.SingleFileName;
            }

            var files = Directory.GetFiles(GetSnapshotsFolderPath());
            if (files.Length == 0) return null;

            int index = Rand.Range(0, files.Length);
            Debug.Log(Debug.Store, "files length: {0} count {1}, selected: {2}", files.Length, files.Count(), index);
            return files[index];
        }

        public string RandomSnapshotFilename() {
            string filename = null;
            do {
                filename = DoGetRandomFilenameFromRootFolder();
                if (filename == null) return null; //no more valid files. sorry, no party.
            } while (filename == null);
            return filename;
        }

        public static string SnapshotNameFor(string snapshotId, string gameName) {
            if (gameName == null) {
                return GetSnapshotsFolderPath() + "/" + snapshotId + ".bp";
            } else {
                return GetSnapshotsFolderPath() + "/" + gameName + "/" + snapshotId + ".bp";
            }
        }

        public static bool SnapshotExists(string snapshotId, string gameName) {
            return File.Exists(SnapshotNameFor(snapshotId, gameName));
        }

        public int StoredSnapshotsCount() {
            return totalFileCount;
        }

        public void RemoveBlueprintWithName(string filename) {
            try {
                File.Delete(filename);
            } catch (Exception) { 
                //can't do anything useful really, just ignore
            }
        }

        public List<string> FilterOutExistingItems(List<string> source, string gamePath = null) {

            List<string> result = new List<string>();

            foreach (string item in source) {
                if (!File.Exists(SnapshotNameFor(item, gamePath))) { 
                    result.Add(item);
                }
            }

            return result;
        }

        public void CheckCacheContents() {

            if (RealRuins_ModSettings.offlineMode) {
                var files = Directory.GetFiles(GetSnapshotsFolderPath());
                foreach (string fileName in files) {
                    if (!fileName.Contains("local")) {
                        File.Delete(fileName); //delete all remote files in offline mode.
                    }
                }
            }
            RecalculateFilesSize();
        }

        public void ClearCache() {

            Directory.Delete(GetSnapshotsFolderPath(), true);
            Directory.CreateDirectory(GetSnapshotsFolderPath());
            totalFilesSize = 0;
        }

        public long TotalSize() {
            return totalFilesSize;
        }

        private void RecalculateFilesSize() {

            var filesList = Directory.GetFiles(GetSnapshotsFolderPath());
            long totalSize = 0;
            foreach (string file in filesList) {
                totalSize += new FileInfo(file).Length;
            }

            totalFilesSize = totalSize;
            totalFileCount = filesList.Length;
        }

        public void CheckCacheSizeLimits() {

            var files = Directory.GetFiles(GetSnapshotsFolderPath());
            List<string> filesList = files.ToList();
            filesList.Sort();
            filesList.Reverse();
            

            long totalSize = 0;
            foreach (string file in filesList) {
                totalSize += new FileInfo(file).Length;
                if (totalSize > RealRuins_ModSettings.diskCacheLimit * 1024 * 1024) {
                    File.Delete(file);
                }
            }
            RecalculateFilesSize();
        }

        public bool CanFireMediumEvent() {
            if (StoredSnapshotsCount() > 0) {
                if (RealRuins_ModSettings.offlineMode) return true;
                else return StoredSnapshotsCount() > 30;
            } else {
                return false;
            }
        }

        public bool CanFireLargeEvent() {
            if (StoredSnapshotsCount() > 0) {
                if (RealRuins_ModSettings.offlineMode) return true;
                else return StoredSnapshotsCount() > 250;
            } else {
                return false;
            }
        }
    }
}
