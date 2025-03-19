//using Assimp;
//using System;
//using System.Collections.Generic;
//using System.IO;
//using System.Numerics;
//using System.Runtime.InteropServices;
//using Veldrid;
//using Veldrid.SPIRV;
//using Matrix4x4 = Assimp.Matrix4x4;

//namespace VeldridSTLViewer // Make sure this matches your project's namespace!
//{
//    public class AssimpModelLoader
//    {
//        // --- P/Invoke Declarations for Assimp ---

//        // Minimal C# representation of aiScene (for now)
//        [StructLayout(LayoutKind.Sequential)]
//        public unsafe struct aiScene
//        {
//            public uint mFlags;
//            public aiNode* mRootNode;
//            public uint mNumMeshes;
//            public aiMesh* mMeshes;
//            public uint mNumMaterials;
//            public IntPtr mMaterials;
//            public uint mNumAnimations;
//            public IntPtr mAnimations;
//            public uint mNumTextures;
//            public IntPtr mTextures;
//            public uint mNumLights;
//            public IntPtr mLights;
//            public uint mNumCameras;
//            public IntPtr mCameras;
//        }

//        // aiMesh - **IMPORTANT: DOUBLE-CHECK AGAINST YOUR ASSIMP HEADERS!**
//        [StructLayout(LayoutKind.Sequential)]
//        public unsafe struct aiMesh
//        {
//            public uint mPrimitiveTypes;
//            public unsafe aiVector3D* mVertices;
//            public unsafe aiVector3D* mNormals;
//            public unsafe aiVector3D* mTangents;
//            public unsafe aiVector3D* mBitangents;
//            public uint mNumVertices;
//            public uint mNumFaces;
//            public unsafe aiFace* mFaces;
//            public IntPtr mMaterialIndex; // <-- Added Material Index - check if your aiMesh has this in C++
//            // ... (Add other mesh members if needed based on your Assimp version) ...
//        }

//        // aiFace
//        [StructLayout(LayoutKind.Sequential)]
//        public struct aiFace
//        {
//            public uint mNumIndices;
//            public unsafe uint* mIndices;
//        }

//        // aiVector3D
//        [StructLayout(LayoutKind.Sequential)]
//        public struct aiVector3D
//        {
//            public float x;
//            public float y;
//            public float z;
//        }

//        // aiNode
//        [StructLayout(LayoutKind.Sequential)]
//        public unsafe struct aiNode
//        {
//            public IntPtr mName;
//            public Matrix4x4 mTransformation;
//            public uint mNumMeshes;
//            public uint* mMeshes;
//            public aiNode* mParent;
//            public uint mNumChildren;
//            public aiNode** mChildren;
//        }


//        // Import function
//        [DllImport("assimp")]
//        public static extern unsafe aiScene* aiImportFile(
//            [MarshalAs(UnmanagedType.LPStr)] string pFile,
//            uint pFlags);

//        // Release function
//        [DllImport("assimp")]
//        public static extern unsafe void aiReleaseImport(aiScene* pScene);

//        // Error string function
//        [DllImport("assimp")]
//        public static extern IntPtr aiGetErrorString();

//        // --- End of P/Invoke Declarations ---


//        internal (VertexRigged[] vertices, ushort[] indices) LoadModelWithAssimp(string path)
//        {
//            Console.WriteLine($"Attempting to load model with P/Invoke: {path}");

//            unsafe // Needed for pointer operations with P/Invoke
//            {
//                aiScene* scenePtr = null; // Initialize scene pointer to null
//                try
//                {
//                    scenePtr = aiImportFile(
//                        path,
//                        (uint)(PostProcessSteps.Triangulate | // Using AssimpNet's enum -  **VERIFY IF THESE ENUM VALUES ARE CORRECT FOR YOUR ASSIMP VERSION!**
//                               PostProcessSteps.GenerateSmoothNormals |
//                               PostProcessSteps.JoinIdenticalVertices |
//                               PostProcessSteps.FixInFacingNormals |
//                               PostProcessSteps.ImproveCacheLocality |
//                               PostProcessSteps.OptimizeMeshes |
//                               PostProcessSteps.CalculateTangentSpace)
//                    );

//                    if (scenePtr == null)
//                    {
//                        // Error handling using aiGetErrorString
//                        IntPtr errorStringPtr = aiGetErrorString();
//                        string errorString = Marshal.PtrToStringAnsi(errorStringPtr);
//                        Console.WriteLine($"P/Invoke Assimp Error importing '{path}': {errorString}");
//                        return (Array.Empty<VertexRigged>(), Array.Empty<ushort>());
//                    }
//                    else
//                    {
//                        Console.WriteLine($"P/Invoke Assimp: Scene loaded successfully from {Path.GetFileName(path)} (but not yet processing data)");
//                    }

//                    aiScene scene = *scenePtr;

//                    List<VertexRigged> vertexList = new List<VertexRigged>();
//                    List<ushort> indexList = new List<ushort>();

//                    if (scene.mNumMeshes > 0)
//                    {
//                        aiMesh* meshArrayPtr = scene.mMeshes;

//                        for (int meshIndex = 0; meshIndex < scene.mNumMeshes; ++meshIndex)
//                        {
//                            aiMesh* meshPtr = &meshArrayPtr[meshIndex];
//                            aiMesh mesh = *meshPtr;

//                            Console.WriteLine($"  Mesh #{meshIndex}: {mesh.mNumVertices} vertices, {mesh.mNumFaces} faces");

//                            // --- Process Vertices ---
//                            if (mesh.mVertices == null) // **NULL CHECK - DEBUGGING**
//                            {
//                                Console.WriteLine($"Error: mesh.mVertices is NULL for mesh #{meshIndex} in {Path.GetFileName(path)}");
//                                continue; // Skip to the next mesh
//                            }
//                            if (mesh.mNumVertices > 0)
//                            {
//                                aiVector3D* vertexArrayPtr = mesh.mVertices;
//                                aiVector3D* normalArrayPtr = mesh.mNormals;

//                                for (int vertexIndex = 0; vertexIndex < mesh.mNumVertices; ++vertexIndex)
//                                {
//                                    if (vertexIndex < 0 || vertexIndex >= mesh.mNumVertices) // **BOUNDS CHECK - DEBUGGING**
//                                    {
//                                        Console.WriteLine($"Error: vertexIndex out of bounds! vertexIndex={vertexIndex}, mesh.mNumVertices={mesh.mNumVertices} for mesh #{meshIndex} in {Path.GetFileName(path)}");
//                                        continue; // Skip to the next vertex
//                                    }
//                                    aiVector3D vertexPos = vertexArrayPtr[vertexIndex]; // **LINE THAT CAUSED AccessViolationException**
//                                    aiVector3D vertexNormal = mesh.mNormals != null ? normalArrayPtr[vertexIndex] : new aiVector3D() { x = 0, y = 1, z = 0 };

//                                    Vector3 pos = new Vector3(vertexPos.x, vertexPos.y, vertexPos.z);
//                                    Vector3 norm = new Vector3(vertexNormal.x, vertexNormal.y, vertexNormal.z);

//                                    Vector3 bary = Vector3.Zero;
//                                    Int4 boneIndices = new Int4(0, 0, 0, 0);
//                                    Vector4 boneWeights = new Vector4(1f, 0f, 0f, 0f);

//                                    vertexList.Add(new VertexRigged(pos, norm, bary, boneIndices, boneWeights));
//                                }
//                            }

//                            // --- Process Faces (Indices) ---
//                            if (mesh.mNumFaces > 0)
//                            {
//                                aiFace* faceArrayPtr = mesh.mFaces;

//                                for (int faceIndex = 0; faceIndex < mesh.mNumFaces; ++faceIndex)
//                                {
//                                    aiFace face = faceArrayPtr[faceIndex];

//                                    if (face.mNumIndices == 3) // Triangles only for now
//                                    {
//                                        for (int i = 0; i < 3; ++i)
//                                        {
//                                            uint index = face.mIndices[i];
//                                            indexList.Add((ushort)index);

//                                            vertexList[vertexList.Count - 3 + i] = vertexList[vertexList.Count - 3 + i] with
//                                            {
//                                                Barycentrics = i switch
//                                                {
//                                                    0 => new Vector3(1f, 0f, 0f),
//                                                    1 => new Vector3(0f, 1f, 0f),
//                                                    _ => new Vector3(0f, 0f, 1f)
//                                                }
//                                            };
//                                        }
//                                    }
//                                    else
//                                    {
//                                        Console.WriteLine($"Warning: Face with {face.mNumIndices} indices found (not a triangle). Skipping face.");
//                                    }
//                                }
//                            }
//                        }
//                    }
//                    else
//                    {
//                        Console.WriteLine($"Warning: No meshes found in scene from {Path.GetFileName(path)}.");
//                    }

//                    // --- Important: Release the scene data ---
//                    aiReleaseImport(scenePtr);

//                    Console.WriteLine($"P/Invoke Assimp: Loaded {vertexList.Count} vertices, {indexList.Count} indices from {Path.GetFileName(path)}");
//                    return (vertexList.ToArray(), indexList.ToArray());
//                }
//                catch (Exception ex)
//                {
//                    Console.WriteLine($"P/Invoke C# Exception in LoadModelWithAssimp: {ex}");
//                    if (scenePtr != null) aiReleaseImport(scenePtr); // Ensure release even on exception
//                    return (Array.Empty<VertexRigged>(), Array.Empty<ushort>());
//                }
//            }
//        }
//    }
//}