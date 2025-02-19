using UnityEngine;
using UnityEngine.AddressableAssets;

namespace Services.AddressablesService
{
    public class AddressableAssetLoader : MonoBehaviour
    {
        [SerializeField] AssetReference _assetReference;
        [SerializeField] bool _forceRelease;
    
        [ContextMenu("Load asset")]
        void Load()
        {
            ServiceContainer.UseService<IAddressableService>((addressableService) =>
            {
                Debug.Log(_assetReference.AssetGUID);
                addressableService.LoadAsset<ScriptableObject>(
                    this,
                    _assetReference,
                    (loadedAsset) => { Debug.Log("Load successfully"); },
                    onFail: (message) => { Debug.Log("Could not load asset, something went wrong"); });
            });
        }

        [ContextMenu("Release asset")]
        void Release()
        {
            ServiceContainer.UseService<IAddressableService>((addressableService) =>
            {
                addressableService.ReleaseAsset(this, _assetReference, _forceRelease);
            });
        }
    }
}