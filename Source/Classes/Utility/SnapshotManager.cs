﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

using System.IO;

using Verse;
using UnityEngine;

namespace RealRuins {


    class SnapshotManager {

        private static HashSet<Timer> timers = new HashSet<Timer>();
        private static void ExecuteAfter(Action action, TimeSpan delay) {
            Timer timer = null;
            timer = new Timer(s =>
            {
                action();
                timer.Dispose();
                lock (timers)
                    timers.Remove(timer);
            }, null, (long)delay.TotalMilliseconds, Timeout.Infinite);
            lock (timers)
                timers.Add(timer);
        }


        private static SnapshotManager instance = null;
        public static SnapshotManager Instance {
            get {
                if (instance == null) {
                    instance = new SnapshotManager();
                }
                return instance;
            }
        }

        public Action<int, int> Progress { set => progress = value; }
        public Action<bool> Completion { set => completion = value; }

        private readonly SnapshotStoreManager storeManager = SnapshotStoreManager.Instance;
        //private concurrentDownloads = 0;

        static Dictionary<string, DateTime> snapshotTimestamps = new Dictionary<string, DateTime>();

        private List<string> snapshotsToLoad = new List<string>();

        private int snapshotsToLoadCount = 0;
        private int loadedSnapshotsCount = 0;
        private int failedSnapshotsCount = 0;
        private bool loadIfExists = true;

        private Action<int, int> progress;
        private Action<bool> completion;

        private bool forceStop = false;


        public void Reset() {
            snapshotsToLoadCount = 0;
            loadedSnapshotsCount = 0;
            failedSnapshotsCount = 0;
            snapshotsToLoad = new List<string>();
        }

        public void Stop() {
            forceStop = true;
        }

        //try to load snapshots until your guts are out
        public void AggressiveLoadSnapshots() {
            APIService service = new APIService();

            Debug.Log(Debug.Store, "Snapshot pool is almost empty, doing some aggressive loading...", true);
            
            service.LoadRandomMapsList(delegate (bool success, List<string> files) {
                if (!success) {
                    Debug.Log(Debug.Store, "Failed loading list of random maps. Rescheduling after 10 seconds");
                    ExecuteAfter(delegate () {
                        AggressiveLoadSnapshots();
                    }, new TimeSpan(0, 0, 20));
                    return;
                }

                Debug.Log(Debug.Store, "Loaded list of {0} elements...", files.Count);
                files = storeManager.FilterOutExistingItems(files);

                foreach (string filename in files) {
                    snapshotsToLoad.Add(filename);
                }

                Debug.Log(Debug.Store, "Loading {0} files...", snapshotsToLoad.Count);
                AggressiveLoadSnaphotsFromList(snapshotsToLoad, null);
            });
        }

        public void AggressiveLoadSnaphotsFromList(List<string> snapshotsToLoad, string gamePath = null, bool loadIfExists = true) {
            this.loadIfExists = loadIfExists;

            if (loadIfExists == false) {
                this.snapshotsToLoad = storeManager.FilterOutExistingItems(snapshotsToLoad, gamePath);
                Debug.Log(Debug.Store, "Filtered snapshots {0} -> {1}", snapshotsToLoad.Count, this.snapshotsToLoad.Count);
            } else {
                this.snapshotsToLoad = snapshotsToLoad; //in case it was call from the outside
            }

            snapshotsToLoadCount = this.snapshotsToLoad.Count;
            var concurrentLoaders = Math.Min(10, snapshotsToLoadCount);

            if (snapshotsToLoadCount > 0) {
                for (int i = 0; i < concurrentLoaders; i++) {
                    LoadNextSnapshot(gamePath);
                }
            } else {
                completion?.Invoke(true);
            }
        }

        public void LoadSomeSnapshots(int concurrent = 1, int retries = 10) {
            if (snapshotsToLoad.Count > 0) return; //don't start loader if there is something still to load

            loadIfExists = true;


            APIService service = new APIService();

            Debug.Log(Debug.Store, "Loading some snapshots...", true);

            service.LoadRandomMapsList(delegate (bool success, List<string> files) {
                if (!success) {
                    Debug.Log(Debug.Store, "Failed loading list of random maps");
                    ExecuteAfter(delegate () {
                        LoadSomeSnapshots(concurrent, retries - 1);
                    }, new TimeSpan(0, 0, 20));
                    return;
                }

                Debug.Log(Debug.Store, "Loaded list of {0} elements...", files.Count);
                files = storeManager.FilterOutExistingItems(files);
            
                foreach (string filename in files) {
                    snapshotsToLoad.Add(filename);
                }

                Debug.Log(Debug.Store, "Loading {0} files...", snapshotsToLoad.Count);

                if (snapshotsToLoad.Count > 0) {
                    for (int i = 0; i < concurrent; i++) {
                        LoadNextSnapshot();
                    }
                }
            });
        }

        private void LoadNextSnapshot(string gamePath = null) {
            if (forceStop) {
                return;
            }

            string next = snapshotsToLoad.Pop();

            Debug.Log(Debug.Store, "Loading snapshot {0}", next);

            APIService service = new APIService();
            service.LoadMap(next, delegate (bool success, byte[] data) {
                if (success) {
                    loadedSnapshotsCount++;
                    storeManager.StoreBinaryData(data, next, gamePath);
                } else {
                    failedSnapshotsCount++;
                    Debug.Warning(Debug.Store, "Failed loading snapshot {0}", next);
                }

                progress?.Invoke(loadedSnapshotsCount, snapshotsToLoadCount);

                if (snapshotsToLoad.Count > 0) {
                    //Debug.Message("Snapshots to load: {0}", snapshotsToLoad);
                    LoadNextSnapshot(gamePath);
                } else {
                    //Debug.Message("Loaded: {0}, failed: {1}, should be: {2}", loadedSnapshotsCount, failedSnapshotsCount, snapshotsToLoadCount);
                    if (loadedSnapshotsCount + failedSnapshotsCount == snapshotsToLoadCount) {
                        completion?.Invoke(true);
                    }
                }
            });
        }

        public void UploadCurrentMapSnapshot() {
            if (Find.CurrentMap == null) {
                return;
            }

            string worldId = (Math.Abs(Find.World.info.persistentRandomValue)).ToString();
            string mapId = Find.CurrentMap.uniqueID.ToString();
            string snapshotId = worldId + mapId;

            if (snapshotTimestamps.ContainsKey(snapshotId) && (DateTime.Now - snapshotTimestamps[snapshotId]).TotalMinutes < 180) {
                return; //skip upload if we're trying to do it more frequent than once per three hours.
            }

            //we actually don't care if something goes wrong. big data, y'know
            snapshotTimestamps[snapshotId] = DateTime.Now;

            SnapshotGenerator generator = new SnapshotGenerator(Find.CurrentMap);
            if (!generator.CanGenerate()) return; //skip if generation is not allowed on some reason (too small area, empty area, whatever)

            string tmpFilename = generator.Generate();

            if (tmpFilename != null) {
                Compressor.ZipFile(tmpFilename);

                if (RealRuins.SingleFile) {
                    SnapshotStoreManager.Instance.StoreBinaryData(File.ReadAllBytes(tmpFilename), "jeluder.bp");
                } else if (RealRuins_ModSettings.offlineMode) {
                    SnapshotStoreManager.Instance.StoreBinaryData(File.ReadAllBytes(tmpFilename), "local-" + snapshotId + ".bp");
                } else {
                    Debug.Log(Debug.Store, "Uploading file {0}", tmpFilename);
                    APIService service = new APIService();
                    service.UploadMap(tmpFilename, delegate (bool success) {
                        File.Delete(tmpFilename);
                        completion?.Invoke(success);
                    });
                }
            }
        }
    }
}
