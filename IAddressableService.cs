using System;
using UnityEngine.AddressableAssets;
using Object = UnityEngine.Object;

namespace Services.AddressablesService
{
    public interface IAddressableService : IService
    {
        void LoadAsset<TObject>(Object owner, AssetReference asset, Action<TObject> onLoad, Action<string> onFail, string customLabel = "") where TObject : Object;
        void LoadUntrackedAsset<TObject>(string path, Action<TObject> onLoad, Action<string> onFail) where TObject : Object;
        void ReleaseAsset(Object owner, AssetReference asset, bool force);
        TObject GetLoadedAssetResult<TObject>(string label) where TObject : Object;
        bool IsAssetAlreadyLoaded(AssetReference asset);
    }
}