using SharpGLTF.Schema2;
using SharpGLTF.Transforms;
using System;
using System.Numerics;

namespace VeldridSTLViewer
{
    public static class GlTFHelper
    {
        public static void PrintModelHierarchy(string gltfPath)
        {
            // Load the model.
            ModelRoot model = ModelRoot.Load(gltfPath);
            Console.WriteLine("Model loaded successfully.");

            // Use the default scene.
            var scene = model.DefaultScene;
            Console.WriteLine($"Default scene has {scene.LogicalParent.LogicalNodes.Count} root nodes.");

            // Recursively print each root node and its children.
            foreach (var root in scene.LogicalParent.LogicalNodes)
            {
                PrintNodeHierarchy(root, Matrix4x4.Identity);
            }
        }

        private static void PrintNodeHierarchy(Node node, Matrix4x4 parentWorld)
        {
            // Compute the local transform from the node.
            Matrix4x4 local = ConvertAffine(node.LocalTransform);
            // Compute the world transform by multiplying with the parent's world transform.
            Matrix4x4 world = parentWorld * local;

            Console.WriteLine($"Node: {node.Name}");
            Console.WriteLine($"World Transform:\n{world}");

            // Recurse for each visual child.
            foreach (var child in node.VisualChildren)
            {
                PrintNodeHierarchy(child, world);
            }
        }

        private static Matrix4x4 ConvertAffine(AffineTransform at)
        {
            // Using the order: Translation * Rotation * Scale.
            return Matrix4x4.CreateTranslation(at.Translation) *
                   Matrix4x4.CreateFromQuaternion(at.Rotation) *
                   Matrix4x4.CreateScale(at.Scale);
        }
    }
}
