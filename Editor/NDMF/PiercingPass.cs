#if PIERCING_NDMF
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
                if (setup.targetRenderer == null) continue;

                try
                {
                    ProcessSetup(setup);
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[PiercingTool] ピアス処理に失敗しました: {setup.gameObject.name}\n{e}");
                }
            }
        }

        private void ProcessSetup(PiercingSetup setup)
        {
            var mesh = MeshGenerator.Generate(setup);

            // 生成したメッシュをRendererに適用
            var smr = setup.GetComponent<SkinnedMeshRenderer>();
            if (smr != null)
            {
                smr.sharedMesh = mesh;

                // ボーン情報をターゲットRendererから設定
                if (!setup.skipBoneWeightTransfer)
                {
                    smr.bones = setup.targetRenderer.bones;
                    smr.rootBone = setup.targetRenderer.rootBone;
                }
            }

#if PIERCING_MODULAR_AVATAR
            SetupBlendShapeSync(setup, mesh);
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

            for (int i = 0; i < piercingMesh.blendShapeCount; i++)
            {
                string shapeName = piercingMesh.GetBlendShapeName(i);
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
            var parts = new System.Collections.Generic.List<string>();
            var current = target;
            while (current != null && current != root)
            {
                parts.Insert(0, current.name);
                current = current.parent;
            }
            return string.Join("/", parts);
        }
#endif
    }
}
#endif
