#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;

public class NativeMeshPayloadAsset : ScriptableObject
{
    public int payloadVersion = 1;
    public string sourceFbxPath;
    public string sourceFbxHash;
    public string payloadHash;
    public string payloadCompression;
    public List<NativeMeshPayloadRenderer> renderers = new List<NativeMeshPayloadRenderer>();
    public List<NativeMeshPayloadBone> bones = new List<NativeMeshPayloadBone>();
    public List<NativeMeshPayloadAuthoringPoseBone> authoringPoseBones = new List<NativeMeshPayloadAuthoringPoseBone>();
}

[Serializable]
public class NativeMeshPayloadRenderer
{
    public string avatarPath;
    public string fbxMeshPath;
    public string meshName;
    public string rendererName;
    public Mesh mesh;
    public Vector3 localPosition;
    public Quaternion localRotation = Quaternion.identity;
    public Vector3 localScale = Vector3.one;
    public string rootBonePath;
    public List<string> bonePaths = new List<string>();
}

[Serializable]
public class NativeMeshPayloadBone
{
    public string path;
    public Vector3 localPosition;
    public Quaternion localRotation;
    public Vector3 localScale;
}

[Serializable]
public class NativeMeshPayloadAuthoringPoseBone
{
    public string path;
    public Vector3 localPositionOffset;
    public Quaternion localRotationDelta = Quaternion.identity;
    public Vector3 localScaleMultiplier = Vector3.one;
}
#endif
