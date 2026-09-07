#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>Checks a real imported reference model against an applied avatar, without modifying either.</summary>
public static class MCBMeshRoundTripVerifier
{
    [Serializable] public sealed class RendererResult
    {
        public string renderer;
        public int vertices, blendshapes, frames, boneCount;
        public bool geometryExact, skinWeightsExact, bonePathsExact, blendshapeFramesExact;
        public bool blendshapeLayoutExact, blendshapePositionsWithinUnityPrecision;
        public double maximumBlendshapePositionError;
        public string[] missingBlendshapes;
        public bool Passed => geometryExact && skinWeightsExact && bonePathsExact
            && blendshapeLayoutExact && blendshapePositionsWithinUnityPrecision;
    }

    public static List<RendererResult> Compare(Transform referenceRoot, Transform appliedRoot)
    {
        var result = new List<RendererResult>();
        foreach (var reference in referenceRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            string path = SmrPathService.GetRelativeTransformPath(referenceRoot, reference.transform);
            var target = appliedRoot.Find(path)?.GetComponent<SkinnedMeshRenderer>();
            if (reference.sharedMesh == null || target?.sharedMesh == null)
                throw new InvalidOperationException("Missing mesh at " + path);
            Mesh expected = reference.sharedMesh, actual = target.sharedMesh;
            var row = new RendererResult { renderer = path, vertices = expected.vertexCount,
                blendshapes = expected.blendShapeCount, boneCount = reference.bones.Length };
            row.geometryExact = expected.vertices.SequenceEqual(actual.vertices)
                && expected.normals.SequenceEqual(actual.normals) && expected.tangents.SequenceEqual(actual.tangents)
                && expected.bindposes.SequenceEqual(actual.bindposes) && expected.subMeshCount == actual.subMeshCount;
            for (int sub = 0; row.geometryExact && sub < expected.subMeshCount; sub++)
                row.geometryExact &= expected.GetTopology(sub) == actual.GetTopology(sub)
                    && expected.GetIndices(sub).SequenceEqual(actual.GetIndices(sub));
            for (int channel = 0; channel < 8; channel++)
            {
                var left = new List<Vector4>(); var right = new List<Vector4>();
                expected.GetUVs(channel, left); actual.GetUVs(channel, right);
                row.geometryExact &= left.SequenceEqual(right);
            }
            using (var left = expected.GetBonesPerVertex())
            using (var right = actual.GetBonesPerVertex())
                row.skinWeightsExact = left.ToArray().SequenceEqual(right.ToArray());
            using (var left = expected.GetAllBoneWeights())
            using (var right = actual.GetAllBoneWeights())
                row.skinWeightsExact &= left.ToArray().SequenceEqual(right.ToArray());
            row.bonePathsExact = reference.bones.Select(b => SmrPathService.GetRelativeTransformPath(referenceRoot, b))
                .SequenceEqual(target.bones.Select(b => SmrPathService.GetRelativeTransformPath(appliedRoot, b)))
                && SmrPathService.GetRelativeTransformPath(referenceRoot, reference.rootBone)
                    == SmrPathService.GetRelativeTransformPath(appliedRoot, target.rootBone)
                && target.bones.All(b => b != null);
            var missing = new List<string>();
            row.blendshapeFramesExact = expected.blendShapeCount == actual.blendShapeCount;
            row.blendshapeLayoutExact = row.blendshapeFramesExact;
            var verticesLeft = new Vector3[expected.vertexCount];
            var verticesRight = new Vector3[actual.vertexCount];
            for (int shape = 0; shape < expected.blendShapeCount; shape++)
            {
                string name = expected.GetBlendShapeName(shape);
                int actualIndex = actual.GetBlendShapeIndex(name);
                if (actualIndex < 0) { missing.Add(name); row.blendshapeFramesExact = row.blendshapeLayoutExact = false; continue; }
                int count = expected.GetBlendShapeFrameCount(shape);
                row.blendshapeFramesExact &= actualIndex == shape && count == actual.GetBlendShapeFrameCount(actualIndex);
                row.blendshapeLayoutExact &= actualIndex == shape && count == actual.GetBlendShapeFrameCount(actualIndex);
                for (int frame = 0; frame < Math.Min(count, actual.GetBlendShapeFrameCount(actualIndex)); frame++)
                {
                    expected.GetBlendShapeFrameVertices(shape, frame, verticesLeft, null, null);
                    actual.GetBlendShapeFrameVertices(actualIndex, frame, verticesRight, null, null);
                    row.blendshapeFramesExact &= verticesLeft.SequenceEqual(verticesRight)
                        && expected.GetBlendShapeFrameWeight(shape, frame) == actual.GetBlendShapeFrameWeight(actualIndex, frame);
                    row.blendshapeLayoutExact &= expected.GetBlendShapeFrameWeight(shape, frame) == actual.GetBlendShapeFrameWeight(actualIndex, frame);
                    for (int vertex = 0; vertex < Math.Min(verticesLeft.Length, verticesRight.Length); vertex++)
                        row.maximumBlendshapePositionError = Math.Max(row.maximumBlendshapePositionError,
                            (verticesLeft[vertex] - verticesRight[vertex]).magnitude);
                    row.frames++;
                }
            }
            // Unity's AddBlendShapeFrame removes near-zero position deltas when
            // normal/tangent deltas do not retain that vertex. Report exactness
            // separately instead of concealing this public API precision limit.
            row.blendshapePositionsWithinUnityPrecision = row.maximumBlendshapePositionError <= 0.00001
                && expected.vertexCount == actual.vertexCount;
            row.missingBlendshapes = missing.ToArray(); result.Add(row);
        }
        return result;
    }
}
#endif
