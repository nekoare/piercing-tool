#if PIERCING_NDMF && PIERCING_VRCSDK && PIERCING_MODULAR_AVATAR
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.Animations;

namespace PiercingTool.Editor
{
    /// <summary>
    /// Viseme BlendShapeをAnimator経由で同期するための
    /// AnimatorController + MA Merge Animatorを生成する。
    /// </summary>
    public static class VisemeAnimatorGenerator
    {
        /// <summary>
        /// ピアスオブジェクトにViseme同期用のAnimator + MA Merge Animatorを設定する。
        /// ピアスメッシュにViseme BlendShapeが存在しない場合は何もしない。
        /// </summary>
        public static void Setup(PiercingSetup setup, Mesh piercingMesh)
        {
            var avatarRoot = setup.transform.root;
            var descriptor = avatarRoot.GetComponent<VRC.SDKBase.VRC_AvatarDescriptor>();

            if (descriptor == null) return;
            if (descriptor.lipSync !=
                VRC.SDKBase.VRC_AvatarDescriptor.LipSyncStyle.VisemeBlendShape)
                return;

            var visemeNames = descriptor.VisemeBlendShapes;
            if (visemeNames == null || visemeNames.Length == 0) return;

            // ピアスメッシュに存在するViseme BlendShapeのみ対象
            var targetVisemes = new List<(int visemeIndex, string shapeName)>();
            for (int i = 0; i < visemeNames.Length; i++)
            {
                if (string.IsNullOrEmpty(visemeNames[i])) continue;
                if (piercingMesh.GetBlendShapeIndex(visemeNames[i]) < 0) continue;
                targetVisemes.Add((i, visemeNames[i]));
            }

            if (targetVisemes.Count == 0) return;

            // ピアスオブジェクトのアバタールートからの相対パス
            string piercingPath = GetRelativePath(setup.transform, avatarRoot);

            // AnimationClip生成
            var clips = CreateVisemeClips(
                visemeNames, targetVisemes, piercingPath);

            // AnimatorController + BlendTree生成
            var controller = CreateAnimatorController(clips);

            // MA Merge Animator設定
            SetupMergeAnimator(setup.gameObject, controller);
        }

        private static AnimationClip[] CreateVisemeClips(
            string[] allVisemeNames,
            List<(int visemeIndex, string shapeName)> targetVisemes,
            string piercingPath)
        {
            // Viseme 0-14 の15クリップを生成
            var clips = new AnimationClip[15];

            for (int v = 0; v < 15; v++)
            {
                var clip = new AnimationClip();
                clip.name = $"Viseme_{v}";

                foreach (var (visemeIndex, shapeName) in targetVisemes)
                {
                    // このVisemeが一致→weight 100、それ以外→weight 0
                    float weight = (visemeIndex == v) ? 100f : 0f;

                    var curve = new AnimationCurve(
                        new Keyframe(0f, weight),
                        new Keyframe(1f / 60f, weight));

                    clip.SetCurve(
                        piercingPath,
                        typeof(SkinnedMeshRenderer),
                        $"blendShape.{shapeName}",
                        curve);
                }

                clips[v] = clip;
            }

            return clips;
        }

        private static AnimatorController CreateAnimatorController(
            AnimationClip[] clips)
        {
            var controller = new AnimatorController();
            controller.name = "PiercingViseme";

            // Visemeパラメータ追加
            controller.AddParameter("Viseme", AnimatorControllerParameterType.Int);

            // レイヤー追加
            controller.AddLayer("PiercingViseme");
            var layer = controller.layers[0];
            layer.defaultWeight = 1f;
            controller.layers = new[] { layer };

            var stateMachine = layer.stateMachine;

            // BlendTree作成
            var blendTree = new BlendTree();
            blendTree.name = "VisemeBlendTree";
            blendTree.blendType = BlendTreeType.Simple1D;
            blendTree.blendParameter = "Viseme";
            blendTree.useAutomaticThresholds = false;

            for (int i = 0; i < clips.Length; i++)
            {
                blendTree.AddChild(clips[i], (float)i);
            }

            // BlendTreeをステートとして追加
            var state = stateMachine.AddState("Viseme", Vector3.zero);
            state.motion = blendTree;
            state.writeDefaultValues = true;
            stateMachine.defaultState = state;

            return controller;
        }

        private static void SetupMergeAnimator(
            GameObject piercingObj,
            AnimatorController controller)
        {
            var mergeAnimator = piercingObj
                .GetComponent<nadena.dev.modular_avatar.core.ModularAvatarMergeAnimator>();
            if (mergeAnimator == null)
                mergeAnimator = piercingObj
                    .AddComponent<nadena.dev.modular_avatar.core.ModularAvatarMergeAnimator>();

            mergeAnimator.animator = controller;
            mergeAnimator.layerType =
                VRC.SDK3.Avatars.Components.VRCAvatarDescriptor.AnimLayerType.FX;
            mergeAnimator.pathMode =
                nadena.dev.modular_avatar.core.MergeAnimatorPathMode.Absolute;
            mergeAnimator.matchAvatarWriteDefaults = true;
        }

        private static string GetRelativePath(Transform target, Transform root)
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
    }
}
#endif
