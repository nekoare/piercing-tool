#if PIERCING_NDMF
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;

namespace PiercingTool.Editor
{
    public class PiercingPass : Pass<PiercingPass>
    {
        protected override void Execute(BuildContext context)
        {
            var setups = context.AvatarRootObject
                .GetComponentsInChildren<PiercingSetup>(true);

            foreach (var setup in setups)
            {
                // プレビューメッシュがクローンに残っている場合、元のメッシュに復元
                var mf = setup.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                {
                    var original = PiercingSetupEditor.FindOriginalMesh(mf.sharedMesh);
                    if (original != null)
                        mf.sharedMesh = original;
                }

                if (setup.targetRenderer == null) continue;

                try
                {
                    ProcessSetup(setup);
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[PiercingTool] ピアス処理に失敗しました: {setup.gameObject.name}\n{e}");
                }

                Object.DestroyImmediate(setup);
            }
        }

        private void ProcessSetup(PiercingSetup setup)
        {
            var mesh = MeshGenerator.Generate(setup);

            // MeshFilter+MeshRenderer → SkinnedMeshRenderer に変換
            var smr = setup.GetComponent<SkinnedMeshRenderer>();
            if (smr == null)
            {
                var mf = setup.GetComponent<MeshFilter>();
                var mr = setup.GetComponent<MeshRenderer>();

                Material[] materials = null;
                if (mr != null)
                    materials = mr.sharedMaterials;

                if (mf != null) Object.DestroyImmediate(mf);
                if (mr != null) Object.DestroyImmediate(mr);

                smr = setup.gameObject.AddComponent<SkinnedMeshRenderer>();

                if (materials != null)
                    smr.sharedMaterials = materials;
            }

            smr.sharedMesh = mesh;

            if (!setup.skipBoneWeightTransfer)
            {
                smr.bones = setup.targetRenderer.bones;
                smr.rootBone = setup.targetRenderer.rootBone;
            }

#if PIERCING_MODULAR_AVATAR
            SetupBlendShapeSync(setup, mesh);
#endif

#if PIERCING_VRCSDK && PIERCING_MODULAR_AVATAR
            VisemeAnimatorGenerator.Setup(setup, mesh);
#endif
        }

#if PIERCING_MODULAR_AVATAR
        private void SetupBlendShapeSync(PiercingSetup setup, Mesh piercingMesh)
        {
            var sync = setup.gameObject
                .GetComponent<nadena.dev.modular_avatar.core.ModularAvatarBlendshapeSync>();

            if (sync == null)
                sync = setup.gameObject
                    .AddComponent<nadena.dev.modular_avatar.core.ModularAvatarBlendshapeSync>();

            sync.Bindings.Clear(); // 再ビルド時の重複防止

#if PIERCING_VRCSDK
            var visemeNames = GetVisemeBlendShapeNames(setup.transform.root);
#else
            HashSet<string> visemeNames = null;
#endif

            for (int i = 0; i < piercingMesh.blendShapeCount; i++)
            {
                string shapeName = piercingMesh.GetBlendShapeName(i);

                // Viseme BlendShapeはAnimatorで駆動するのでSyncから除外
                if (visemeNames != null && visemeNames.Contains(shapeName))
                    continue;

                var binding = new nadena.dev.modular_avatar.core.BlendshapeBinding
                {
                    ReferenceMesh = new nadena.dev.modular_avatar.core.AvatarObjectReference
                    {
                        referencePath = GetRelativePath(
                            setup.targetRenderer.transform,
                            setup.transform.root)
                    },
                    Blendshape = shapeName,
                    LocalBlendshape = shapeName
                };
                sync.Bindings.Add(binding);
            }
        }

        private string GetRelativePath(Transform target, Transform root)
        {
            var parts = new List<string>();
            var current = target;
            while (current != null && current != root)
            {
                parts.Insert(0, current.name);
                current = current.parent;
            }
            return string.Join("/", parts);
        }
#endif

#if PIERCING_VRCSDK
        private static HashSet<string> GetVisemeBlendShapeNames(Transform avatarRoot)
        {
            var descriptor = avatarRoot.GetComponent<VRC.SDKBase.VRC_AvatarDescriptor>();
            if (descriptor == null) return null;
            if (descriptor.lipSync != VRC.SDKBase.VRC_AvatarDescriptor.LipSyncStyle.VisemeBlendShape)
                return null;
            if (descriptor.VisemeBlendShapes == null || descriptor.VisemeBlendShapes.Length == 0)
                return null;

            return new HashSet<string>(descriptor.VisemeBlendShapes);
        }
#endif
    }
}
#endif
