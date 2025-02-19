using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Services.AddressablesService.Exceptions;
using R3;
using UnityEditor;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using Object = UnityEngine.Object;

namespace Services.AddressablesService.Implementation
{
    public class AddressableService : IAddressableService
    {
        public string Name => "AddressableService";

        bool _initialized = false;
        
        Dictionary<string, AssetReference> _LoadedAssetReferenceGUID;
        Dictionary<AssetReference, List<Object>> _LoadedAssets;

        Dictionary<AssetReference, string> _AssetsWithCustomLabel;
        Dictionary<string, AsyncOperationHandle> _LoadedAssetsResult;
        
        List<string> _LoadingAsset;

        IDisposable _unloadAssetThread;
        CancellationTokenSource _cancellationTokenSource;
        
        public async Task AsyncSetup()
        {
            var operation = Addressables.InitializeAsync();
            operation.Completed += _ =>
            {
#if UNITY_EDITOR
               logs = new List<string>();
#endif
                _LoadedAssets = new Dictionary<AssetReference, List<Object>>();
                _LoadedAssetReferenceGUID = new Dictionary<string, AssetReference>();

                _AssetsWithCustomLabel = new Dictionary<AssetReference, string>(); 
                _LoadedAssetsResult = new Dictionary<string, AsyncOperationHandle>();
                
                _LoadingAsset = new List<string>();

                _initialized = true;
                
                StartUnloadUnusedAssetsThread();
            };

            await UniTask.WaitUntil(() => _initialized);
        }

        ~AddressableService()
        {
            if (null != _unloadAssetThread)
            {
                Debug.Log("Unload unused assets thread cancelled");
                _unloadAssetThread.Dispose();
                _cancellationTokenSource.Cancel();
            }
        }

        string GetAssetLabel(AssetReference assetReference)
        {
            string foundLabel = string.Empty;
            if (!_AssetsWithCustomLabel.TryGetValue(assetReference, out foundLabel))
            {
                foundLabel = assetReference.AssetGUID;
            }
            
            return foundLabel;
        }

        void TryUnloadUnusedAssets()
        {
            List<AssetReference> unusedAssets = new List<AssetReference>();
            Dictionary<AssetReference, List<Object>> updatedAssets = 
                new Dictionary<AssetReference, List<Object>>(_LoadedAssets);
            
            foreach (var data in _LoadedAssets)
            {
                AssetReference key = data.Key;
                List<Object> owners = data.Value;

                updatedAssets[key] = owners.Where(owner => default != owner).ToList();
                if (updatedAssets[key].Count == 0)
                {
                    unusedAssets.Add(key);
                }
            }

            _LoadedAssets = updatedAssets;

            Array.ForEach(unusedAssets.ToArray(), assetReference =>
            {
                Log($"Releasing asset { assetReference.Asset.name } due to no one is using it anymore...");
                assetReference.ReleaseAsset();
                _LoadedAssets.Remove(assetReference);
                _LoadedAssetReferenceGUID.Remove(assetReference.AssetGUID);
                _AssetsWithCustomLabel.Remove(assetReference);
                _LoadedAssetsResult.Remove(GetAssetLabel(assetReference));
            });
        }
        
        void KeepTryingUnloadUnusedAssets()
        {
            _unloadAssetThread = Observable.Interval(TimeSpan.FromSeconds(5))
                .Subscribe(_ =>
                {
                    try
                    {
                        TryUnloadUnusedAssets();
                    }
                    catch(Exception e)
                    {
                        Log($"Something went wrong on try to unload unused assets. Exception caught:\n\n {e.GetType().Name}:{e.Message}");
                        _cancellationTokenSource.Cancel();
                    }
                });
        }

        void StartUnloadUnusedAssetsThread()
        {
            _cancellationTokenSource = new CancellationTokenSource();
            KeepTryingUnloadUnusedAssets();
        }

        void RegisterLoadedAsset(AssetReference asset, Object owner, AsyncOperationHandle operationHandle, string customLabel = "")
        {
            if (IsAssetAlreadyLoaded(asset))
            {
                AssetReference registeredAsset = _LoadedAssetReferenceGUID[asset.AssetGUID];
                if(!_LoadedAssets[registeredAsset].Contains(owner))
                    _LoadedAssets[registeredAsset].Add(owner);
            }
            else
            {
                if (string.IsNullOrEmpty(customLabel))
                    customLabel = asset.AssetGUID;
                
                _LoadedAssetReferenceGUID.Add(asset.AssetGUID, asset);
                _LoadedAssets.Add(asset, new List<Object> { owner });
                _LoadedAssetsResult.Add(customLabel, operationHandle);
            }
        }

        void MarkAssetAsLoading(AssetReference assetReference)
        {
            _LoadingAsset.Add(assetReference.AssetGUID);
        }
        
        void MarkAssetAsLoading(string assetReference)
        {
            _LoadingAsset.Add(assetReference);
        }

        bool IsLoadingAsset(AssetReference assetReference)
        {
            return _LoadingAsset.Contains(assetReference.AssetGUID);
        }

        bool IsLoadingAsset(string key)
        {
            return _LoadingAsset.Contains(key);
        }

        void UnmarkAssetAsLoading(AssetReference assetReference)
        {
            _LoadingAsset.Remove(assetReference.AssetGUID);
        }

        void Log(string message)
        {
#if UNITY_EDITOR
            Debug.Log($"[{Name}]:{message}");
            logs.Add(message);
#endif
        }

        public async void LoadAsset<TObject>(Object owner, AssetReference asset, Action<TObject> onLoad, Action<string> onFail = null, string customLabel = "")
            where TObject : Object
        {
            //In case this asset is already being loaded.
            await UniTask.WaitUntil(()=> !IsLoadingAsset(asset));
            
            if (IsAssetAlreadyLoaded(asset))
            {
                AssetReference registeredAsset = _LoadedAssetReferenceGUID[asset.AssetGUID];
                onLoad.Invoke((TObject)registeredAsset.OperationHandle.Result);
                RegisterLoadedAsset(registeredAsset, owner, _LoadedAssetsResult[GetAssetLabel(asset)], customLabel);
                return;
            }

            AsyncOperationHandle task = asset.LoadAssetAsync<TObject>();

            MarkAssetAsLoading(asset);
            
            task.Completed += (asyncOperationHandle) =>
            {
                if (null == asyncOperationHandle.OperationException)
                {
                    onLoad?.Invoke((TObject)asyncOperationHandle.Result);
                    RegisterLoadedAsset(asset, owner, asyncOperationHandle, customLabel);
                }
                else
                {
                    _LoadedAssets[asset].Remove(owner);
                    onFail?.Invoke(asyncOperationHandle.OperationException.Message);
                }

                UnmarkAssetAsLoading(asset);
            };
        }

        public async void LoadUntrackedAsset<TObject>(string path, Action<TObject> onLoad, Action<string> onFail) where TObject : Object
        {
            try
            {
                TObject operation = await Addressables.LoadAssetAsync<TObject>(path);
                onLoad?.Invoke(operation);
            }
            catch(Exception e)
            {
                Debug.LogException(e);
                onFail?.Invoke(e.Message);
            }
        }

        public void ReleaseAsset(Object owner, AssetReference assetReference, bool force)
        {
            if(!IsAssetAlreadyLoaded(assetReference))
                return;
                
            if (force)
            {
                assetReference.ReleaseAsset();
                _LoadedAssets.Remove(assetReference);
                _LoadedAssetReferenceGUID.Remove(assetReference.AssetGUID);
                return;
            }
            
            AssetReference registeredAsset = _LoadedAssetReferenceGUID[assetReference.AssetGUID];
            _LoadedAssets[registeredAsset].Remove(owner);
        }

        public bool IsAssetAlreadyLoaded(AssetReference asset)
        {
            return _LoadedAssetReferenceGUID.Keys.Contains(asset.AssetGUID);
        }
        
        public bool IsAssetAlreadyLoaded(string assetUrl)
        {
            return _LoadedAssetReferenceGUID.Keys.Contains(assetUrl);
        }

        public TObject GetLoadedAssetResult<TObject>(string label)
            where TObject : Object
        {
            try
            {
                return _LoadedAssetsResult[label].Result as TObject;
            }
            catch (KeyNotFoundException e)
            {
               throw new InexistentAssetWithLabelException(label);
            }
            catch (InvalidCastException e)
            {
                throw e;
            }
        }

#if UNITY_EDITOR
        List<bool> inspectingAssets;
        List<string> logs;
        bool showLogs = false;
        
        public void DebugService()
        {
            GUIStyle redLabel = new GUIStyle(GUI.skin.label);
            redLabel.normal.textColor = Color.red;
            redLabel.hover.textColor = Color.red;
            redLabel.focused.textColor = Color.red;
            redLabel.active.textColor = Color.red;
            
            GUIStyle greenLabel = new GUIStyle(GUI.skin.label);
            greenLabel.normal.textColor = Color.green;
            greenLabel.hover.textColor = Color.green;
            greenLabel.focused.textColor = Color.green;
            greenLabel.active.textColor = Color.green;
            
            GUILayout.BeginHorizontal();
            GUILayout.Label("Loaded Assets");
            bool canShowLogs = GUILayout.Button(EditorGUIUtility.IconContent("d_UnityEditor.ConsoleWindow"), GUILayout.Width(25));
            if (canShowLogs)
                showLogs = !showLogs;
            GUILayout.EndHorizontal();
            
            bool isUnloadUnusedAssetsThreadStillRunning = !_cancellationTokenSource.IsCancellationRequested;
            GUIStyle guiStyle_isUnloadUnusedAssetsThreadStillRunning =
                isUnloadUnusedAssetsThreadStillRunning ? greenLabel : redLabel;
            GUILayout.BeginHorizontal();
            GUILayout.Label("Trying to unload unused assets (Thread): ");
            GUILayout.Label(isUnloadUnusedAssetsThreadStillRunning.ToString(), guiStyle_isUnloadUnusedAssetsThreadStillRunning);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            
            if (!showLogs)
                Debug_ShowLoadedAssets();
            else
                Debug_ShowLogs();
        }

        void Debug_ShowLoadedAssets()
        {

            GUILayout.Space(10f);
            GUIStyle centeredLabel = new GUIStyle(GUI.skin.label);
            centeredLabel.alignment = TextAnchor.MiddleCenter;

            GUIStyle redLabel = new GUIStyle(GUI.skin.label);
            redLabel.normal.textColor = Color.red;
            redLabel.hover.textColor = Color.red;
            redLabel.focused.textColor = Color.red;
            redLabel.active.textColor = Color.red;
            
            if(null == inspectingAssets)
                inspectingAssets = new System.Collections.Generic.List<bool>();
            
            if (null == _LoadedAssets ||  _LoadedAssets.Keys.Count == 0)
            {
                GUILayout.Label("There isn't any addressed asset loaded in to the memory right now.", centeredLabel);
                return;
            }
            
            foreach (AssetReference loadedAsset in _LoadedAssets.Keys)
            {
                int id = System.Array.IndexOf(_LoadedAssets.Keys.ToArray(), loadedAsset);
                if (inspectingAssets.Count < _LoadedAssets.Keys.Count)
                    inspectingAssets.Add(false);
                
                bool foldout = inspectingAssets[id];
                inspectingAssets[id] = EditorGUILayout.Foldout(foldout, loadedAsset.Asset.name);
                if (inspectingAssets[id])
                {
                    EditorGUI.indentLevel++;

                    if (_LoadedAssets[loadedAsset].Count == 0)
                        EditorGUILayout.LabelField("No usage, to be unloaded", redLabel);
                    
                    foreach (object owner in _LoadedAssets[loadedAsset])
                    {
                        if (null != owner)
                        {
                            Object unityObject = (Object)owner;
                            if (null != unityObject)
                                EditorGUILayout.LabelField(unityObject.name + $"({ unityObject.GetType() })");
                            else
                                EditorGUILayout.LabelField("Missing reference (To be removed on list)", redLabel);
                        }
                    }
                    EditorGUI.indentLevel--;
                }
            }
        }

        void Debug_ShowLogs()
        {
            string logs = string.Join( '\n', this.logs.ToArray());
            GUILayout.TextArea(logs, GUILayout.Height(400));
        }
#endif
    }
}