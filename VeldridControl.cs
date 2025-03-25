using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Veldrid;
using Veldrid.SPIRV;
using PixelFormat = Veldrid.PixelFormat;
using Assimp;
using Assimp.Configs;
using Matrix4x4 = System.Numerics.Matrix4x4;
using System.Text;
using Vortice.Mathematics;
using Viewport = Veldrid.Viewport;
using Point = Avalonia.Point;
using System.Reflection;
using Veldrid.MetalBindings;
using SharpGLTF.Schema2;
using SharpGLTF.Transforms;
using SharpGLTF.IO; // For ModelRoot.ReadGLB
using System.Linq;
using Texture = Veldrid.Texture;
using Buffer = System.Buffer;
using Node = SharpGLTF.Schema2.Node;
using ReactiveUI;
using Tmds.DBus.Protocol;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;

namespace VeldridSTLViewer
{


    // -------------------------
    // Helper Struct for 4 ints.
    // -------------------------
    public struct Int4
    {
        public int X, Y, Z, W;
        public Int4(int x, int y, int z, int w)
        {
            X = x; Y = y; Z = z; W = w;
        }
    }

    // -------------------------
    // Vertex Structures
    // -------------------------
    public struct VertexRigged
    {
        // 3 floats for Position, 3 for Normal, 3 for Barycentrics, 4 ints for BoneIndices, 4 floats for BoneWeights.
        public const uint SizeInBytes = 68;
        public Vector3 Position;
        public Vector3 Normal;
        public Vector3 Barycentrics;
        public Int4 BoneIndices;
        public Vector4 BoneWeights;
        public VertexRigged(Vector3 position, Vector3 normal, Vector3 barycentrics, Int4 boneIndices, Vector4 boneWeights)
        {
            Position = position;
            Normal = normal;
            Barycentrics = barycentrics;
            BoneIndices = boneIndices;
            BoneWeights = boneWeights;
        }
    }

    public struct VertexPositionColor
    {
        public const uint SizeInBytes = 24;
        public Vector3 Position;
        public Vector3 Color;
        public VertexPositionColor(Vector3 position, Vector3 color)
        {
            Position = position;
            Color = color;
        }
    }

    // -------------------------
    // Model Class
    // -------------------------
    public class Model
    {
        public DeviceBuffer VertexBuffer { get; set; }
        public DeviceBuffer IndexBuffer { get; set; }
        public int VertexCount { get; set; }
        public int IndexCount { get; set; }
        public Matrix4x4 Transform { get; set; }

        // Additional information for bone grouping:
        public string Name { get; set; }
        public int BoneIndex { get; set; }  // 0 = base, 1 = upperarm, 2 = forearm, etc.
        public Vector3 Center { get; set; } // Computed center of the model's vertices (in world space, after applying Transform)
        public bool IsSkinned { get; set; }

    }

    // -------------------------
    // VeldridControl.cs (Main)
    // -------------------------
    public class VeldridControl : Control
    {
        public static readonly StyledProperty<IBrush> BackgroundProperty =
            AvaloniaProperty.Register<VeldridControl, IBrush>(nameof(Background));
        public IBrush Background
        {
            get => GetValue(BackgroundProperty);
            set => SetValue(BackgroundProperty, value);
        }

        private GraphicsDevice _graphicsDevice;
        private CommandList _commandList;
        private List<Model> _models = new List<Model>();

        private Shader[] _modelShaders;
        private Pipeline _modelPipeline;


        private DeviceBuffer _mvpBuffer;
        private ResourceLayout _mvpLayout;
        private ResourceSet _mvpResourceSet;
        private WriteableBitmap _avaloniaBitmap;
        private Framebuffer _offscreenFramebuffer;
        private Veldrid.Texture _offscreenColorTexture;
        private Texture _offscreenDepthTexture;
        private Texture _stagingTexture;
        private bool _resourcesCreated = false;

        // Bone uniforms.
        private DeviceBuffer _boneBuffer;
        private ResourceLayout _boneLayout;
        private ResourceSet _boneResourceSet;
        private ModelRoot _cachedModelRoot;
        private Skin _cachedSkin;

        private Dictionary<string, int> _boneMapping;

        private void CacheModelData(string gltfPath)
        {
            _cachedModelRoot = ModelRoot.Load(gltfPath);
            _cachedSkin = _cachedModelRoot.LogicalSkins.FirstOrDefault();
            if (_cachedSkin == null)
            {
                Console.WriteLine("No skin data found in model.");
            }
            else
            {
                // Build the mapping.
                _boneMapping = new Dictionary<string, int>();
                for (int i = 0; i < _cachedSkin.Joints.Count; i++)
                {
                    // Lowercase the bone name so you can compare case–insensitively.
                    string boneName = _cachedSkin.Joints[i].Name.ToLower();
                    _boneMapping[boneName] = i;
                }
            }
        }

        // Grid.
        private DeviceBuffer _gridVertexBuffer;
        private DeviceBuffer _gridIndexBuffer;
        private Pipeline _gridPipeline;
        private ResourceSet _gridResourceSet;
        private ResourceLayout _gridResourceLayout;
        private Shader[] _gridShaders;

        // Camera and Input.
        private CameraController _cameraController;
        private InputState _input = new InputState();
        private Point _previousMousePosition = new Point(0, 0);

        // --- Shader Strings ---
        private const string ModelVertexCode = @"
#version 450

layout(location = 0) in vec3 Position;
layout(location = 1) in vec3 Normal;
layout(location = 2) in vec3 Barycentrics;
layout(location = 3) in ivec4 BoneIndices;
layout(location = 4) in vec4 BoneWeights;

layout(location = 0) out vec3 v_Bary;
layout(location = 1) out vec3 v_Normal;
layout(location = 2) out vec3 v_FragPos;

layout(set = 0, binding = 0) uniform MVP {
    // For skeletal meshes, the CPU should pass in Model = Identity.
    mat4 Model;
    mat4 View;
    mat4 Projection;
};

layout(std140, set = 1, binding = 0) uniform Bones {
    mat4 BoneMatrices[15];
};

void main()
{
    // Compute the skinning matrix – if the mesh is skinned this matters;
    // for non–skinned meshes (which you want to follow a bone) the CPU must have left the vertices in bind–pose.
    mat4 skinMatrix = BoneWeights.x * BoneMatrices[BoneIndices.x] +
                      BoneWeights.y * BoneMatrices[BoneIndices.y] +
                      BoneWeights.z * BoneMatrices[BoneIndices.z] +
                      BoneWeights.w * BoneMatrices[BoneIndices.w];

    vec4 worldPosition = Model * (skinMatrix * vec4(Position, 1.0));
    gl_Position = Projection * View * worldPosition;

    v_Bary = Barycentrics;
    v_Normal = normalize(mat3(skinMatrix) * Normal);
    v_FragPos = worldPosition.xyz;
}
";
        private const string ModelFragmentCode = @"
#version 450

layout(location = 0) in vec3 v_Bary;
layout(location = 1) in vec3 v_Normal;
layout(location = 2) in vec3 v_FragPos;
layout(location = 0) out vec4 fsout_Color;

const vec3 lightPos = vec3(10.0, 10.0, 10.0);
const vec3 lightColor = vec3(1.0, 1.0, 1.0);
const vec3 ambientColor = vec3(0.4, 0.4, 0.4);
const vec3 viewPos = vec3(0, 0, 0);
const float shininess = 32.0;

void main()
{
    vec3 norm = normalize(v_Normal);
    vec3 lightDir = normalize(lightPos - v_FragPos);
    float diff = max(dot(norm, lightDir), 0.0);
    vec3 diffuse = diff * lightColor;

    vec3 ambient = ambientColor;

    vec3 viewDir = normalize(viewPos - v_FragPos);
    vec3 reflectDir = reflect(-lightDir, norm);
    float spec = pow(max(dot(viewDir, reflectDir), 0.0), shininess);
    vec3 specular = spec * lightColor;

    fsout_Color = vec4(ambient + diffuse + specular, 1.0);
}";
        private const string GridVertexCode = @"
#version 450
layout(location = 0) in vec3 Position;
layout(location = 1) in vec3 Color;
layout(location = 0) out vec3 fsin_Color;
layout(set = 0, binding = 0) uniform MVP {
    mat4 Model;
    mat4 View;
    mat4 Projection;
};
void main()
{
    gl_Position = Projection * View * Model * vec4(Position, 1.0);
    fsin_Color = Color;
}";
        private const string GridFragmentCode = @"
#version 450
layout(location = 0) in vec3 fsin_Color;
layout(location = 0) out vec4 fsout_Color;
void main()
{
    fsout_Color = vec4(fsin_Color, 1.0);
}";

        public VeldridControl()
        {
            this.Focusable = true;
            this.Background = Brushes.Transparent;
            this.AttachedToVisualTree += OnAttachedToVisualTree;
            this.DetachedFromVisualTree += OnDetachedFromVisualTree;
            this.PointerMoved += OnPointerMoved;
            this.PointerPressed += OnPointerPressed;
            this.PointerReleased += OnPointerReleased;
            this.KeyDown += OnKeyDown;
            this.KeyUp += OnKeyUp;
        }

        private void OnAttachedToVisualTree(object sender, VisualTreeAttachmentEventArgs e)
        {
            InitializeVeldrid();
            Dispatcher.UIThread.Post(() =>
            {
                InvalidateMeasure();
                InvalidateArrange();
                Measure(Bounds.Size);
                Arrange(new Rect(Bounds.Size));
            }, DispatcherPriority.Loaded);
        }
        private void OnDetachedFromVisualTree(object sender, VisualTreeAttachmentEventArgs e)
        {
            DisposeResources();
        }

        // Input handlers.
        private void OnPointerMoved(object? sender, PointerEventArgs e)
        {
            if (e != null)
            {
                Point currentPos = e.GetPosition(this);
                _input.MouseDelta = currentPos - _previousMousePosition;
                _previousMousePosition = currentPos;
            }
        }
        private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e != null)
            {
                _input.SetMouseDown(e.GetCurrentPoint(this).Properties.PointerUpdateKind.GetMouseButton(), true);
                this.Focus();
            }
        }
        private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (e != null)
            {
                _input.SetMouseDown(e.GetCurrentPoint(this).Properties.PointerUpdateKind.GetMouseButton(), false);
            }
        }
        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            _input.SetKeyDown(e.Key, true);
        }
        private void OnKeyUp(object? sender, KeyEventArgs e)
        {
            _input.SetKeyDown(e.Key, false);
        }
        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            Console.WriteLine($"OnSizeChanged: NewSize = {e.NewSize}, Bounds = {Bounds}");
            if (_resourcesCreated && e.NewSize.Width > 0 && e.NewSize.Height > 0)
            {
                ResizeResources();
            }
        }

        private void InitializeVeldrid()
        {
            try
            {
                GraphicsDeviceOptions options = new GraphicsDeviceOptions
                {
                    PreferStandardClipSpaceYDirection = true,
                    PreferDepthRangeZeroToOne = true,
                    SyncToVerticalBlank = true,
                    ResourceBindingModel = ResourceBindingModel.Default,
                    SwapchainDepthFormat = PixelFormat.B8_G8_R8_A8_UNorm,
                    Debug = true
                };
                _graphicsDevice = GraphicsDevice.CreateVulkan(options);
                CreateResources();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error initializing Veldrid: {ex}");
            }
        }

        private void ResizeResources()
        {
            if (_graphicsDevice == null) return;
            _graphicsDevice.WaitForIdle();

            _offscreenFramebuffer?.Dispose();
            _offscreenColorTexture?.Dispose();
            _offscreenDepthTexture?.Dispose();
            _stagingTexture?.Dispose();
            _gridPipeline?.Dispose();
            if (_gridShaders != null)
            {
                foreach (var shader in _gridShaders)
                    shader.Dispose();
            }
            _gridVertexBuffer?.Dispose();
            _gridIndexBuffer?.Dispose();
            _gridResourceSet?.Dispose();
            _gridResourceLayout?.Dispose();
            _avaloniaBitmap?.Dispose();
            _avaloniaBitmap = null;

            CreateOffscreenFramebuffer();
            CreateStagingTexture();
            CreateAvaloniaBitmap();
            CreateGridResources();
            _cameraController.UpdateAspectRatio((float)Bounds.Width / (float)Bounds.Height);
            Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background);
        }

        private void CreateResources()
        {
            if (_graphicsDevice == null) return;
            ResourceFactory factory = _graphicsDevice.ResourceFactory;

            // Create offscreen framebuffer.
            CreateOffscreenFramebuffer();
            _commandList = factory.CreateCommandList();

            // Create MVP uniform buffer.
            _mvpBuffer = factory.CreateBuffer(new BufferDescription(16 * 64, BufferUsage.UniformBuffer));
            _mvpLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("MVP", ResourceKind.UniformBuffer, ShaderStages.Vertex)));
            _mvpResourceSet = factory.CreateResourceSet(new ResourceSetDescription(_mvpLayout, _mvpBuffer));

            // Create bone uniform buffer.
            _boneBuffer = factory.CreateBuffer(new BufferDescription(16 * 64, BufferUsage.UniformBuffer));
            _boneLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("Bones", ResourceKind.UniformBuffer, ShaderStages.Vertex)));
            _boneResourceSet = factory.CreateResourceSet(new ResourceSetDescription(_boneLayout, _boneBuffer));

            // --- Load models from folder using Assimp ---
            // Get the directory where the executing assembly (your .exe) is located.
            string assemblyPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            // Construct the full path to the "newarm" folder.
            string modelsFolder = Path.Combine(assemblyPath, "newarm");
            int boneIndex = 0; // default is "base"
            if (Directory.Exists(modelsFolder))
            {
                // In CreateResources (or similar initialization routine)
                string robotArmPath = Path.Combine(modelsFolder, "robotarm.glb");
                if (File.Exists(robotArmPath))
                {
                    CacheModelData(robotArmPath);
                    _cachedModelRoot = ModelRoot.Load(robotArmPath);

                    var loadedModels = LoadGLTFModelsFromModelRoot(_cachedModelRoot, 0);
                    foreach (var m in loadedModels)
                    {
                        _models.Add(m);
                    }
                    Matrix4x4[] boneTransforms = ComputeBoneTransforms(_cachedModelRoot);
                    if (boneTransforms.Length > 0)
                    {
                        _graphicsDevice.UpdateBuffer(_boneBuffer, 0, boneTransforms);
                        Console.WriteLine($"Updated bone buffer with {boneTransforms.Length} bone matrices.");
                    }
                }


                else
                {
                    Console.WriteLine($"robotarm.gltf not found in {modelsFolder}");
                }
            }
            else
            {
                Console.WriteLine($"Models folder not found: {modelsFolder}");
            }


            // Create model pipeline using the rigged vertex layout.
            VertexLayoutDescription riggedVertexLayout = new VertexLayoutDescription(
                new VertexElementDescription("Position", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
                new VertexElementDescription("Normal", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
                new VertexElementDescription("Barycentrics", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
                new VertexElementDescription("BoneIndices", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Int4),
                new VertexElementDescription("BoneWeights", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4)
            );
            ShaderDescription modelVSDesc = new ShaderDescription(ShaderStages.Vertex, Encoding.UTF8.GetBytes(ModelVertexCode), "main");
            ShaderDescription modelFSDesc = new ShaderDescription(ShaderStages.Fragment, Encoding.UTF8.GetBytes(ModelFragmentCode), "main");
            _modelShaders = factory.CreateFromSpirv(modelVSDesc, modelFSDesc);
            GraphicsPipelineDescription modelPipelineDesc = new GraphicsPipelineDescription
            {
                BlendState = BlendStateDescription.SingleOverrideBlend,
                RasterizerState = new RasterizerStateDescription(
                    cullMode: FaceCullMode.Back,
                    fillMode: PolygonFillMode.Solid,
                    frontFace: FrontFace.CounterClockwise,
                    depthClipEnabled: true,
                    scissorTestEnabled: false),
                DepthStencilState = new DepthStencilStateDescription(
                    depthTestEnabled: true,
                    depthWriteEnabled: true,
                    comparisonKind: ComparisonKind.LessEqual
                ),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new ResourceLayout[] { _mvpLayout, _boneLayout },
                ShaderSet = new ShaderSetDescription(new VertexLayoutDescription[] { riggedVertexLayout }, _modelShaders),
                Outputs = _offscreenFramebuffer.OutputDescription
            };
            _modelPipeline = factory.CreateGraphicsPipeline(modelPipelineDesc);

            CreateStagingTexture();
            CreateAvaloniaBitmap();
            CreateGridResources();

            // Initialize camera and input.
            _cameraController = new CameraController((float)Bounds.Width / (float)Bounds.Height);
            _input = new InputState();
            // Adjust camera so that the scene (grid and models) are fully visible.
            // Here we position the camera far back and high.
            _cameraController.SetCameraPosition(new Vector3(-558.46857f, 411.7341f, 413.71497f));
            // Set the camera rotation so that it looks horizontally (pitch = 0).
            _cameraController.SetCameraRotation(2.8599985f, -0.23999998f);

            _resourcesCreated = true;
        }




        //private Matrix4x4 ComputeWorldTransform(Node node)
        //{
        //    // Use the matrix provided by SharpGLTF.
        //    Matrix4x4 local = (Matrix4x4)node.LocalTransform.Matrix;
        //    if (node.LogicalParent == null || !(node.LogicalParent is Node))
        //        return local;
        //    Node parentNode = (Node)(object)node.LogicalParent;
        //    return ComputeWorldTransform(parentNode) * local;
        //}



        private Matrix4x4[] ComputeBoneTransforms(ModelRoot model)
        {
            var skin = model.LogicalSkins.FirstOrDefault();
            if (skin == null)
            {
                Console.WriteLine("No skin found in model.");
                return new Matrix4x4[0];
            }

            int count = skin.Joints.Count;
            Matrix4x4[] boneTransforms = new Matrix4x4[count];

            for (int i = 0; i < count; i++)
            {
                boneTransforms[i] = GetBoneFinalTransform(i);
            }

            return boneTransforms;
        }



        /// <summary>
        /// Loads a glTF file and creates one “master” Model (i.e. GPU buffers) for each unique mesh primitive.
        /// For every node that references that same primitive, a new Model instance is created that
        /// reuses the buffers (so you don’t duplicate geometry in GPU memory) but gets its own world transform.
        /// </summary>

        // Helper: Returns the bone index to assign based on the node's name.
        // Helper: Returns the bone index to assign based on the node's name.
        private int GetAssignedBoneIndex(string nodeName)
        {
            string lowerName = nodeName.ToLower();
            int boneIdx = 0; // default fallback

            // Adjust these string comparisons to match your actual bone names.
            if (lowerName.Contains("groundbase"))
            {
                if (_boneMapping.TryGetValue("baseabovebone", out int idx))
                    boneIdx = idx;
            }
            else if (lowerName.Contains("camerahorizontalmovement"))
            {
                if (_boneMapping.TryGetValue("cameraleftrightrotation", out int idx))
                    boneIdx = idx;
            }
            else if (lowerName.Contains("cameravirticalmovement"))
            {
                if (_boneMapping.TryGetValue("camerapivotupdownbone", out int idx))
                    boneIdx = idx;
            }
            // Second branch: the arm ladder.
            else if (lowerName.Contains("rotatorbase"))
            {
                if (_boneMapping.TryGetValue("rotatorbasebone", out int idx))
                    boneIdx = idx;
            }
            else if (lowerName.Contains("lowerarm"))
            {
                if (_boneMapping.TryGetValue("lowerarmlowerbone", out int idx))
                    boneIdx = idx;
            }
            else if (lowerName.Contains("middlearm"))
            {
                if (_boneMapping.TryGetValue("upperarmlowerbone", out int idx))
                    boneIdx = idx;
            }
            else if (lowerName.Contains("rotatorwrist") ||
                     lowerName.Contains("rwgripperrotatorjoint"))
            {
                if (_boneMapping.TryGetValue("wristbone", out int idx))
                    boneIdx = idx;
            }
            else if (lowerName.Contains("lefthandgripper"))
            {
                if (_boneMapping.TryGetValue("leftgripbone", out int idx))
                    boneIdx = idx;
            }
            else if (lowerName.Contains("righthandgripper"))
            {
                if (_boneMapping.TryGetValue("rightgripbone", out int idx))
                    boneIdx = idx;
            }

            return boneIdx;
        }

        // Updated method to load models from a ModelRoot.
        // This method traverses the entire scene graph (using LogicalChildren)
        // and assigns each mesh the computed world transform.
        private List<Model> LoadGLTFModelsFromModelRoot(ModelRoot modelRoot, int defaultBoneIndex)
        {
            var models = new List<Model>();
            // A cache to avoid creating duplicate GPU buffers for primitives reused in multiple nodes.
            var loadedPrimitives = new Dictionary<(SharpGLTF.Schema2.Mesh, int), (DeviceBuffer vb, DeviceBuffer ib, int vertexCount, int indexCount)>();

            // Helper to compute the center of a set of vertices.
            Vector3 ComputeCenter(VertexRigged[] vertices)
            {
                Vector3 min = new Vector3(float.MaxValue);
                Vector3 max = new Vector3(float.MinValue);
                foreach (var v in vertices)
                {
                    min = Vector3.Min(min, v.Position);
                    max = Vector3.Max(max, v.Position);
                }
                return (min + max) / 2;
            }

            // Process a node recursively.
            void ProcessNode(Node node)
            {
                // Compute the node’s world transform using our helper.
                Matrix4x4 worldTransform = ComputeNodeWorldMatrix(node);

                // If the node has a mesh, process each primitive.
                if (node.Mesh != null)
                {
                    int assignedBoneIndex = GetAssignedBoneIndex(node.Name);
                    for (int primIndex = 0; primIndex < node.Mesh.Primitives.Count; primIndex++)
                    {
                        var primitive = node.Mesh.Primitives[primIndex];
                        var key = (node.Mesh, primIndex);
                        if (!loadedPrimitives.TryGetValue(key, out var buffers))
                        {
                            // Get POSITION and NORMAL arrays.
                            Vector3[] positions = primitive.VertexAccessors["POSITION"].AsVector3Array().ToArray();
                            Vector3[] normals = primitive.VertexAccessors.ContainsKey("NORMAL")
                                ? primitive.VertexAccessors["NORMAL"].AsVector3Array().ToArray()
                                : Enumerable.Repeat(Vector3.UnitY, positions.Length).ToArray();

                            // Get index data (or generate sequential indices if not provided).
                            ushort[] primIndices = (primitive.IndexAccessor != null)
                                ? primitive.IndexAccessor.AsIndicesArray().Select(x => (ushort)x).ToArray()
                                : Enumerable.Range(0, positions.Length).Select(i => (ushort)i).ToArray();

                            // Determine if skinning data is present.
                            bool hasJoints = primitive.VertexAccessors.ContainsKey("JOINTS_0");
                            bool hasWeights = primitive.VertexAccessors.ContainsKey("WEIGHTS_0");

                            int vertexCountLocal = positions.Length;
                            int[][] jointsData = null;
                            float[][] weightsData = null;
                            if (hasJoints && hasWeights)
                            {
                                jointsData = primitive.VertexAccessors["JOINTS_0"]
                                    .AsVector4Array()
                                    .Select(v => new int[] { (int)v.X, (int)v.Y, (int)v.Z, (int)v.W })
                                    .ToArray();
                                weightsData = primitive.VertexAccessors["WEIGHTS_0"]
                                    .AsVector4Array()
                                    .Select(v => new float[] { v.X, v.Y, v.Z, v.W })
                                    .ToArray();
                            }

                            // Build vertices for this primitive.
                            var vertices = new VertexRigged[vertexCountLocal];
                            for (int i = 0; i < vertexCountLocal; i++)
                            {
                                Int4 boneIndices;
                                Vector4 boneWeights;
                                if (hasJoints && hasWeights)
                                {
                                    boneIndices = new Int4(
                                        jointsData[i][0],
                                        jointsData[i][1],
                                        jointsData[i][2],
                                        jointsData[i][3]);
                                    boneWeights = new Vector4(
                                        weightsData[i][0],
                                        weightsData[i][1],
                                        weightsData[i][2],
                                        weightsData[i][3]);
                                }
                                else
                                {
                                    // For non-skinned meshes, use the assigned bone index.
                                    boneIndices = new Int4(assignedBoneIndex, 0, 0, 0);
                                    boneWeights = new Vector4(1f, 0f, 0f, 0f);
                                }

                                vertices[i] = new VertexRigged(
                                    positions[i],
                                    normals[i],
                                    new Vector3(1, 0, 0), // barycentrics (can be adjusted)
                                    boneIndices,
                                    boneWeights);
                            }

                            // Create GPU buffers.
                            ResourceFactory factory = _graphicsDevice.ResourceFactory;
                            DeviceBuffer vbNew = factory.CreateBuffer(new BufferDescription(
                                (uint)(vertices.Length * VertexRigged.SizeInBytes),
                                BufferUsage.VertexBuffer));
                            _graphicsDevice.UpdateBuffer(vbNew, 0, vertices);

                            DeviceBuffer ibNew = factory.CreateBuffer(new BufferDescription(
                                (uint)(primIndices.Length * sizeof(ushort)),
                                BufferUsage.IndexBuffer));
                            _graphicsDevice.UpdateBuffer(ibNew, 0, primIndices);

                            buffers = (vbNew, ibNew, vertices.Length, primIndices.Length);
                            loadedPrimitives[key] = buffers;

                            Vector3 center = ComputeCenter(vertices);
                            models.Add(new Model
                            {
                                Name = node.Name + $"_prim{primIndex}",
                                BoneIndex = assignedBoneIndex,
                                VertexBuffer = vbNew,
                                IndexBuffer = ibNew,
                                VertexCount = vertices.Length,
                                IndexCount = primIndices.Length,
                                Transform = worldTransform, // Use the computed world transform.
                                Center = center
                            });
                        }
                        else
                        {
                            // If already loaded, add an instance with the current node's transform.
                            models.Add(new Model
                            {
                                Name = node.Name + $"_prim{primIndex}_inst",
                                BoneIndex = GetAssignedBoneIndex(node.Name),
                                VertexBuffer = buffers.vb,
                                IndexBuffer = buffers.ib,
                                VertexCount = buffers.vertexCount,
                                IndexCount = buffers.indexCount,
                                Transform = worldTransform,
                                Center = Vector3.Zero
                            });
                        }
                    }
                }

                // Process child nodes recursively.
                foreach (var child in node.VisualChildren)
                {
                    ProcessNode(child);
                }
            }

            // Start processing from all root nodes in the model.
            foreach (var root in modelRoot.LogicalNodes)
            {
                ProcessNode(root);
            }
            return models;
        }
        private void CreateStagingTexture()
        {
            ResourceFactory factory = _graphicsDevice.ResourceFactory;
            _stagingTexture = factory.CreateTexture(TextureDescription.Texture2D(
                (uint)Math.Max(1, Bounds.Width),
                (uint)Math.Max(1, Bounds.Height),
                1, 1,
                PixelFormat.R8_G8_B8_A8_UNorm,
                TextureUsage.Staging));
        }

        private void CreateAvaloniaBitmap()
        {
            var pixelFormat = Avalonia.Platform.PixelFormat.Rgba8888;
            var alphaFormat = Avalonia.Platform.AlphaFormat.Premul;
            _avaloniaBitmap = new WriteableBitmap(
                new Avalonia.PixelSize((int)Math.Max(1, Bounds.Width), (int)Math.Max(1, Bounds.Height)),
                new Avalonia.Vector(96, 96),
                pixelFormat,
                alphaFormat);
        }

        private void CreateOffscreenFramebuffer()
        {
            ResourceFactory factory = _graphicsDevice.ResourceFactory;
            _offscreenColorTexture = factory.CreateTexture(TextureDescription.Texture2D(
                (uint)Math.Max(1, Bounds.Width),
                (uint)Math.Max(1, Bounds.Height),
                1, 1,
                PixelFormat.R8_G8_B8_A8_UNorm,
                TextureUsage.RenderTarget | TextureUsage.Sampled));
            _offscreenDepthTexture = factory.CreateTexture(TextureDescription.Texture2D(
                (uint)Math.Max(1, Bounds.Width),
                (uint)Math.Max(1, Bounds.Height),
                1, 1,
                PixelFormat.D24_UNorm_S8_UInt,
                TextureUsage.DepthStencil));
            _offscreenFramebuffer = factory.CreateFramebuffer(new FramebufferDescription(_offscreenDepthTexture, _offscreenColorTexture));
        }

        private void CreateGridResources()
        {
            ResourceFactory factory = _graphicsDevice.ResourceFactory;
            // Create a larger grid spanning from -80 to +80 on X and Z, placed at y = -0.1.
            VertexPositionColor[] gridVertices = new VertexPositionColor[]
            {
                new VertexPositionColor(new Vector3(-80f, -0.1f, -80f), new Vector3(0.5f,0.5f,0.5f)),
                new VertexPositionColor(new Vector3(80f, -0.1f, -80f), new Vector3(0.5f,0.5f,0.5f)),
                new VertexPositionColor(new Vector3(80f, -0.1f, 80f), new Vector3(0.5f,0.5f,0.5f)),
                new VertexPositionColor(new Vector3(-80f, -0.1f, 80f), new Vector3(0.5f,0.5f,0.5f))
            };

            ushort[] gridIndices = new ushort[]
            {
                0,1, 1,2, 2,3, 3,0
            };

            _gridVertexBuffer = factory.CreateBuffer(new BufferDescription((uint)(gridVertices.Length * VertexPositionColor.SizeInBytes), BufferUsage.VertexBuffer));
            _graphicsDevice.UpdateBuffer(_gridVertexBuffer, 0, gridVertices);

            _gridIndexBuffer = factory.CreateBuffer(new BufferDescription((uint)(gridIndices.Length * sizeof(ushort)), BufferUsage.IndexBuffer));
            _graphicsDevice.UpdateBuffer(_gridIndexBuffer, 0, gridIndices);

            ShaderDescription gridVSDesc = new ShaderDescription(ShaderStages.Vertex, Encoding.UTF8.GetBytes(GridVertexCode), "main");
            ShaderDescription gridFSDesc = new ShaderDescription(ShaderStages.Fragment, Encoding.UTF8.GetBytes(GridFragmentCode), "main");
            _gridShaders = factory.CreateFromSpirv(gridVSDesc, gridFSDesc);

            VertexLayoutDescription gridVertexLayout = new VertexLayoutDescription(
                new VertexElementDescription("Position", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
                new VertexElementDescription("Color", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3)
            );

            _gridResourceLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("MVP", ResourceKind.UniformBuffer, ShaderStages.Vertex)));

            _gridResourceSet = factory.CreateResourceSet(new ResourceSetDescription(_gridResourceLayout, _mvpBuffer));

            GraphicsPipelineDescription gridPipelineDesc = new GraphicsPipelineDescription
            {
                BlendState = BlendStateDescription.SingleOverrideBlend,
                RasterizerState = new RasterizerStateDescription(
                    cullMode: FaceCullMode.None,
                    fillMode: PolygonFillMode.Solid,
                    frontFace: FrontFace.CounterClockwise,
                    depthClipEnabled: true,
                    scissorTestEnabled: false),
                PrimitiveTopology = PrimitiveTopology.LineList,
                ResourceLayouts = new ResourceLayout[] { _gridResourceLayout },
                ShaderSet = new ShaderSetDescription(new VertexLayoutDescription[] { gridVertexLayout }, _gridShaders),
                Outputs = _offscreenFramebuffer.OutputDescription
            };
            _gridPipeline = factory.CreateGraphicsPipeline(gridPipelineDesc);
        }

        // Update the MVP and bone uniform buffers.
        // Example method to update the bone transforms hierarchically.
        public static Matrix4x4 CreateOrientation(Vector3 dir, Vector3 up, float rollAngle)
        {
            if (dir.Length() == 0f)
                return Matrix4x4.Identity;

            // Use 'dir' as the forward (y) direction.
            Vector3 yAxis = Vector3.Normalize(dir);

            // If up is nearly parallel to dir, pick an alternative.
            if (MathF.Abs(Vector3.Dot(yAxis, up)) > 0.999f)
                up = MathF.Abs(yAxis.Z) < 0.99f ? Vector3.UnitZ : Vector3.UnitX;

            // Compute right and recalculated up.
            Vector3 xAxis = Vector3.Normalize(Vector3.Cross(up, yAxis));
            Vector3 zAxis = Vector3.Normalize(Vector3.Cross(xAxis, yAxis));

            // Build a matrix whose rows are the new axes:
            Matrix4x4 tm = new Matrix4x4(
                xAxis.X, xAxis.Y, xAxis.Z, 0,
                yAxis.X, yAxis.Y, yAxis.Z, 0,
                zAxis.X, zAxis.Y, zAxis.Z, 0,
                0, 0, 0, 1);

            // Apply a roll (rotation about the forward/yAxis) if needed.
            if (rollAngle != 0)
                tm *= Matrix4x4.CreateFromAxisAngle(yAxis, rollAngle);

            return tm;
        }


        private void UpdateBoneTransforms()
        {
            if (_cachedSkin == null)
            {
                Console.WriteLine("No cached skin data available. Cannot update bones.");
                return;
            }

            int count = _cachedSkin.Joints.Count;
            Matrix4x4[] boneTransforms = new Matrix4x4[count];

            // Traverse each bone and apply hierarchical transformations
            for (int i = 0; i < count; i++)
            {
                var joint = _cachedSkin.Joints[i];
                Matrix4x4 parentTransform = joint.LogicalParent != null
    ? _cachedSkin.Joints.FirstOrDefault(j => j.Name == joint.LogicalParent.DefaultScene.Name)?.WorldMatrix ?? Matrix4x4.Identity
    : Matrix4x4.Identity;


                // Compute final transform for the bone
                boneTransforms[i] = GetBoneFinalTransform(i);

            }

            // Update bone uniform buffer
            _graphicsDevice.UpdateBuffer(_boneBuffer, 0, boneTransforms);
            Console.WriteLine("Bone uniform buffer updated.");
        }
        /// <summary>
        /// Recursively computes the final transform for the bone at boneIndex.
        /// </summary>
        private Matrix4x4 ComputeWorldTransform(Node node)
        {
            Matrix4x4 local = node.LocalTransform.Matrix;
            if (node.LogicalParent == null || node.LogicalParent is SharpGLTF.Schema2.ModelRoot)
                return local;
            // Cast LogicalParent safely (assuming it’s a Node)
            var parentNode = (Node)(object)node.LogicalParent;
            return ComputeWorldTransform(parentNode) * local;
        }

        private Matrix4x4 GetBoneFinalTransform(int boneIndex)
        {
            var joint = _cachedSkin.Joints[boneIndex];
            // Extract translation from the joint's WorldMatrix.
            Vector3 translation = new Vector3(joint.WorldMatrix.M41, joint.WorldMatrix.M42, joint.WorldMatrix.M43);
            Matrix4x4 translationMatrix = Matrix4x4.CreateTranslation(translation);

            // Debug output to verify the translation.
            Console.WriteLine($"Bone {joint.Name} translation: {translation.X:F2} {translation.Y:F2} {translation.Z:F2}");

            return translationMatrix;
        }
        // Recursively computes the world transform for a given node.
        private Matrix4x4 ComputeNodeWorldMatrix(Node node)
        {
            // If the node’s parent is the ModelRoot, then its world transform is its local transform.
            if (node.LogicalParent is ModelRoot)
                return node.LocalMatrix;
            else
            {
                // Cast the LogicalParent to Node (it should be, if it’s part of the scene).
                var parent = (Node)node.VisualParent;
                return ComputeNodeWorldMatrix(parent) * node.LocalMatrix;
            }
        }


        // Helper method to format a Matrix4x4 as a string.
        private string MatrixToString(Matrix4x4 m)
        {
            return $"{m.M11:F2} {m.M12:F2} {m.M13:F2} {m.M14:F2}\n" +
                   $"{m.M21:F2} {m.M22:F2} {m.M23:F2} {m.M24:F2}\n" +
                   $"{m.M31:F2} {m.M32:F2} {m.M33:F2} {m.M34:F2}\n" +
                   $"{m.M41:F2} {m.M42:F2} {m.M43:F2} {m.M44:F2}";
        }


        private Vector3 ComputeGroupPivot(List<Model> models)
        {
            //if (models == null || models.Count == 0)
            //    return Vector3.Zero;

            Vector3 groupMin = new Vector3(float.MaxValue);
            Vector3 groupMax = new Vector3(float.MinValue);
            foreach (var model in models)
            {
                // model.Center should be in world space; if not, transform it by model.Transform.
                groupMin = Vector3.Min(groupMin, model.Center);
                groupMax = Vector3.Max(groupMax, model.Center);
            }
            return groupMin + groupMax;
        }


        private void UpdateMVP()
        {
            _cameraController.Update(0.016f, _input);
            Matrix4x4[] mvp = _cameraController.GetMVPMatrices(
                _models.Count > 0 ? _models[0].Transform : Matrix4x4.Identity);
            _graphicsDevice.UpdateBuffer(_mvpBuffer, 0, mvp);

            // Call our hierarchical bone update.
            UpdateBoneTransforms();
        }


        public override void Render(Avalonia.Media.DrawingContext context)
        {
            if (!_resourcesCreated)
                return;

            DrawScene();

            Dispatcher.UIThread.InvokeAsync(() =>
            {
                using (var locked = _avaloniaBitmap.Lock())
                {
                    var map = _graphicsDevice.Map<byte>(_stagingTexture, MapMode.Read);
                    unsafe
                    {
                        Buffer.MemoryCopy((void*)map.MappedResource.Data, (void*)locked.Address,
                                          (int)map.MappedResource.SizeInBytes,
                                          (int)map.MappedResource.SizeInBytes);
                    }
                    _graphicsDevice.Unmap(_stagingTexture);
                }
                //Console.WriteLine($"{_cameraController}");
                context.DrawImage(_avaloniaBitmap, new Rect(0, 0, Bounds.Width, Bounds.Height));
                base.Render(context);
            }, DispatcherPriority.Render);

            Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background);
            _input.ClearDelta();
        }

        private void DrawScene()
        {
            UpdateMVP();

            _commandList.Begin();
            _commandList.SetFramebuffer(_offscreenFramebuffer);
            _commandList.ClearColorTarget(0, new RgbaFloat(0, 0, 0, 1));
            _commandList.ClearDepthStencil(1f);
            _commandList.SetViewport(0, new Veldrid.Viewport(0, 0, (float)Bounds.Width, (float)Bounds.Height, 0, 1));
            _commandList.SetScissorRect(0, 0, 0, (uint)Bounds.Width, (uint)Bounds.Height);

            // --- Draw grid with identity transform ---
            Matrix4x4[] gridMVP = _cameraController.GetMVPMatrices(Matrix4x4.Identity);
            _graphicsDevice.UpdateBuffer(_mvpBuffer, 0, gridMVP);
            _commandList.SetPipeline(_gridPipeline);
            _commandList.SetGraphicsResourceSet(0, _mvpResourceSet);
            _commandList.SetVertexBuffer(0, _gridVertexBuffer);
            _commandList.SetIndexBuffer(_gridIndexBuffer, IndexFormat.UInt16);
            _commandList.DrawIndexed((uint)(_gridIndexBuffer.SizeInBytes / sizeof(ushort)), 1, 0, 0, 0);

            // --- Draw each model using its own transform ---
            foreach (var model in _models)
            {
                Matrix4x4 modelMatrix = model.IsSkinned ? Matrix4x4.Identity : model.Transform;
                Matrix4x4[] modelMVP = _cameraController.GetMVPMatrices(modelMatrix);
                _graphicsDevice.UpdateBuffer(_mvpBuffer, 0, modelMVP);
                _commandList.SetPipeline(_modelPipeline);
                _commandList.SetGraphicsResourceSet(0, _mvpResourceSet);
                _commandList.SetGraphicsResourceSet(1, _boneResourceSet);
                _commandList.SetVertexBuffer(0, model.VertexBuffer);
                _commandList.SetIndexBuffer(model.IndexBuffer, IndexFormat.UInt16);
                _commandList.DrawIndexed((uint)model.IndexCount, 1, 0, 0, 0);
            }


            _commandList.CopyTexture(_offscreenColorTexture, _stagingTexture);
            _commandList.End();
            _graphicsDevice.SubmitCommands(_commandList);
        }

        private void DisposeResources()
        {
            _resourcesCreated = false;
            _modelPipeline?.Dispose();
            if (_modelShaders != null)
            {
                foreach (var shader in _modelShaders)
                    shader.Dispose();
            }
            _commandList?.Dispose();
            foreach (var model in _models)
            {
                model.VertexBuffer.Dispose();
                model.IndexBuffer.Dispose();
            }
            _models.Clear();
            _mvpBuffer?.Dispose();
            _mvpResourceSet?.Dispose();
            _mvpLayout?.Dispose();
            _boneBuffer?.Dispose();
            _boneResourceSet?.Dispose();
            _boneLayout?.Dispose();
            _offscreenFramebuffer?.Dispose();
            _offscreenColorTexture?.Dispose();
            _offscreenDepthTexture?.Dispose();
            _stagingTexture?.Dispose();
            _gridPipeline?.Dispose();
            if (_gridShaders != null)
            {
                foreach (var shader in _gridShaders)
                    shader.Dispose();
            }
            _gridVertexBuffer?.Dispose();
            _gridIndexBuffer?.Dispose();
            _gridResourceSet?.Dispose();
            _gridResourceLayout?.Dispose();
            _graphicsDevice?.Dispose();
            _avaloniaBitmap = null;
        }


    }

    // -------------------------
    // Unrigged Vertex Structure (for reference)
    // -------------------------
    public struct VertexPositionNormalBary
    {
        public const uint SizeInBytes = 36;
        public Vector3 Position;
        public Vector3 Normal;
        public Vector3 Barycentrics;
        public VertexPositionNormalBary(Vector3 position, Vector3 normal, Vector3 barycentrics)
        {
            Position = position;
            Normal = normal;
            Barycentrics = barycentrics;
        }
    }
}
