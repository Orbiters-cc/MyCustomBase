#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEngine;

namespace MCBEditorUtils
{
    public static class MeshFinder
    {
        /// <summary>
        /// Finds a SkinnedMeshRenderer by name, prioritizing the shallowest match under the root.
        /// This avoids picking up nested meshes (like those in Armature) when a root-level one exists.
        /// </summary>
        public static SkinnedMeshRenderer FindMeshPrioritizingRoot(Transform root, string meshName)
        {
            if (root == null || string.IsNullOrEmpty(meshName)) return null;

            var allSmrs = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            return allSmrs
                .Where(s => s != null && s.gameObject.name.Equals(meshName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(s => GetDepthUnderRoot(root, s.transform))
                .FirstOrDefault();
        }

        /// <summary>
        /// Gets all SkinnedMeshRenderers in the hierarchy.
        /// </summary>
        public static SkinnedMeshRenderer[] GetAllSkinnedMeshRenderers(Transform root)
        {
            if (root == null) return Array.Empty<SkinnedMeshRenderer>();
            return root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        }

        private static int GetDepthUnderRoot(Transform root, Transform target)
        {
            if (root == null || target == null) return int.MaxValue;

            int depth = 0;
            for (var current = target; current != null; current = current.parent)
            {
                if (current == root) return depth;
                depth++;
            }

            return int.MaxValue;
        }
    }
}
#endif
