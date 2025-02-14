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
using System.Text;
using Veldrid;
using Veldrid.SPIRV;
using PixelFormat = Veldrid.PixelFormat;
using Assimp;
using Assimp.Configs;
using Matrix4x4 = System.Numerics.Matrix4x4;

namespace VeldridSTLViewer
{
    public class VeldridControl : Control
    {
        public static readonly StyledProperty<IBrush> BackgroundProperty =
            AvaloniaProperty.Register<VeldridControl, IBrush>(nameof(Background));
        public IBrush Background
        {
            get => GetValue(BackgroundProperty);
            set => SetValue(BackgroundProperty, value);
        }

        // Rendering fields.
        private GraphicsDevice _graphicsDevice;
        private CommandList _commandList;

        // List of models loaded from files.
        private List<Model> _models = new List<Model>();

        // We'll use one pipeline for the models (fill+outline via barycentrics).
        private Shader[] _modelShaders;
        private Pipeline _modelPipeline;

        private DeviceBuffer _mvpBuffer;
        private ResourceLayout _mvpLayout;
        private ResourceSet _mvpResourceSet;
        private WriteableBitmap _avaloniaBitmap;
        private Framebuffer _offscreenFramebuffer;
        private Texture _offscreenColorTexture;
        private Texture _offscreenDepthTexture;
        private Texture _stagingTexture;
        private bool _resourcesCreated = false;

        // Grid resources.
        private DeviceBuffer _gridVertexBuffer;
        private DeviceBuffer _gridIndexBuffer;
        private Pipeline _gridPipeline;
        private ResourceSet _gridResourceSet;
        private ResourceLayout _gridResourceLayout;
        private Shader[] _gridShaders;

        // Camera and input.
        private CameraController _cameraController;
        private InputState _input = new InputState();
        private Avalonia.Point _previousMousePosition = new Avalonia.Point(0, 0);

        // --- Shader Code Strings for the model using barycentrics ---
        private const string ModelVertexCode = @"
#version 450
layout(location = 0) in vec3 Position;
layout(location = 1) in vec3 Normal;
layout(location = 2) in vec3 Barycentrics;

layout(location = 0) out vec3 v_Bary;
layout(location = 1) out vec3 v_Normal;
layout(location = 2) out vec3 v_FragPos;

layout(set = 0, binding = 0) uniform MVP {
    mat4 Model;
    mat4 View;
    mat4 Projection;
};

void main()
{
    vec4 worldPosition = Model * vec4(Position, 1.0);
    gl_Position = Projection * View * worldPosition;
    
    v_Bary = Barycentrics;
    v_Normal = normalize(mat3(Model) * Normal);
    v_FragPos = worldPosition.xyz;
}";
        private const string ModelFragmentCode = @"
#version 450
layout(location = 0) in vec3 v_Bary;
layout(location = 1) in vec3 v_Normal;
layout(location = 2) in vec3 v_FragPos;

layout(location = 0) out vec4 fsout_Color;

// Light properties.
const vec3 lightPos = vec3(10.0, 10.0, 10.0);
const vec3 lightColor = vec3(1.0, 1.0, 1.0);
const vec3 ambientColor = vec3(0.4, 0.4, 0.4);
const vec3 viewPos = vec3(2.0, 2.0, 2.0);
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
    
    vec3 color = ambient + diffuse + specular;
    fsout_Color = vec4(color, 1.0);
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

        // Input event handlers.
        private void OnPointerMoved(object? sender, PointerEventArgs e)
        {
            if (e != null)
            {
                Avalonia.Point currentPos = e.GetPosition(this);
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
                    SwapchainDepthFormat = PixelFormat.R32_G32_B32_A32_Float,
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

            // Create uniform buffer for MVP (3 matrices).
            _mvpBuffer = factory.CreateBuffer(new BufferDescription(3 * 64, BufferUsage.UniformBuffer));
            _mvpLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("MVP", ResourceKind.UniformBuffer, ShaderStages.Vertex)));
            _mvpResourceSet = factory.CreateResourceSet(new ResourceSetDescription(_mvpLayout, _mvpBuffer));

            // --- Load multiple models from folder using Assimp ---
            // Change this folder path as needed.
            string modelsFolder = @"D:\myscripts\dotnetapplications\VeldridSTLViewer\newarm";
            if (Directory.Exists(modelsFolder))
            {
                string[] modelFiles = Directory.GetFiles(modelsFolder, "*.*", SearchOption.AllDirectories);
                Console.WriteLine($"Found {modelFiles.Length} model files in folder: {modelsFolder}");
                foreach (var file in modelFiles)
                {
                    var (modelVerts, modelIndices) = LoadModelWithAssimp(file);
                    if (modelVerts.Length == 0)
                    {
                        Console.WriteLine($"No vertices loaded from {file}. Skipping.");
                        continue;
                    }
                    // Compute a model matrix for this file so it is centered, scaled, and rotated.
                    Matrix4x4 modelMatrix = ComputeModelMatrix(modelVerts);
                    DeviceBuffer vertexBuffer = factory.CreateBuffer(new BufferDescription((uint)(modelVerts.Length * VertexPositionNormalBary.SizeInBytes), BufferUsage.VertexBuffer));
                    _graphicsDevice.UpdateBuffer(vertexBuffer, 0, modelVerts);
                    DeviceBuffer indexBuffer = factory.CreateBuffer(new BufferDescription((uint)(modelIndices.Length * sizeof(ushort)), BufferUsage.IndexBuffer));
                    _graphicsDevice.UpdateBuffer(indexBuffer, 0, modelIndices);
                    _models.Add(new Model
                    {
                        VertexBuffer = vertexBuffer,
                        IndexBuffer = indexBuffer,
                        VertexCount = modelVerts.Length,
                        IndexCount = modelIndices.Length,
                        Transform = modelMatrix
                    });
                }
                Console.WriteLine($"Loaded {_models.Count} models.");
            }
            else
            {
                Console.WriteLine($"Models folder not found: {modelsFolder}");
            }
            // --- End loading models ---

            // Create model pipeline.
            VertexLayoutDescription vertexLayout = new VertexLayoutDescription(
                new VertexElementDescription("Position", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
                new VertexElementDescription("Normal", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
                new VertexElementDescription("Barycentrics", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3)
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
                ResourceLayouts = new ResourceLayout[] { _mvpLayout },
                ShaderSet = new ShaderSetDescription(new VertexLayoutDescription[] { vertexLayout }, _modelShaders),
                Outputs = _offscreenFramebuffer.OutputDescription
            };
            _modelPipeline = factory.CreateGraphicsPipeline(modelPipelineDesc);

            CreateStagingTexture();
            CreateAvaloniaBitmap();
            CreateGridResources();

            // Initialize camera and input.
            _cameraController = new CameraController((float)Bounds.Width / (float)Bounds.Height);
            _input = new InputState();
            _cameraController.SetCameraPosition(new Vector3(2f, 2f, 2f));
            _cameraController.SetCameraRotation(0, 0);

            _resourcesCreated = true;
        }

        // Assimp loader that creates unique barycentrics per triangle.
        private (VertexPositionNormalBary[] vertices, ushort[] indices) LoadModelWithAssimp(string path)
        {
            var importer = new AssimpContext();
            var postProcessSteps = PostProcessSteps.Triangulate |
                                   PostProcessSteps.GenerateSmoothNormals |
                                   PostProcessSteps.JoinIdenticalVertices |
                                   PostProcessSteps.FixInFacingNormals |
                                   PostProcessSteps.ImproveCacheLocality |
                                   PostProcessSteps.OptimizeMeshes |
                                   PostProcessSteps.CalculateTangentSpace;
            try
            {
                var scene = importer.ImportFile(path, postProcessSteps);
                if (scene == null || scene.MeshCount == 0)
                    throw new InvalidOperationException("No valid meshes found in file.");

                List<VertexPositionNormalBary> vertexList = new List<VertexPositionNormalBary>();
                List<ushort> indexList = new List<ushort>();

                // For each mesh, iterate over faces and create new vertices for each triangle.
                foreach (var mesh in scene.Meshes)
                {
                    for (int f = 0; f < mesh.Faces.Count; f++)
                    {
                        var face = mesh.Faces[f];
                        if (face.Indices.Count != 3)
                            continue;

                        // For each vertex in the face, assign barycentrics based on the vertex order.
                        for (int j = 0; j < 3; j++)
                        {
                            int idx = face.Indices[j];
                            var vertex = mesh.Vertices[idx];
                            Vector3 pos = new Vector3(vertex.X, vertex.Y, vertex.Z);
                            Vector3 norm = mesh.HasNormals ? new Vector3(mesh.Normals[idx].X, mesh.Normals[idx].Y, mesh.Normals[idx].Z) : Vector3.UnitZ;
                            Vector3 bary = j switch
                            {
                                0 => new Vector3(1f, 0f, 0f),
                                1 => new Vector3(0f, 1f, 0f),
                                _ => new Vector3(0f, 0f, 1f)
                            };
                            vertexList.Add(new VertexPositionNormalBary(pos, norm, bary));
                        }
                        ushort baseIndex = (ushort)(vertexList.Count - 3);
                        indexList.Add(baseIndex);
                        indexList.Add((ushort)(baseIndex + 1));
                        indexList.Add((ushort)(baseIndex + 2));
                    }
                }
                Console.WriteLine($"Assimp: Loaded {vertexList.Count} vertices from {Path.GetFileName(path)}");
                return (vertexList.ToArray(), indexList.ToArray());
            }
            catch (AssimpException ex)
            {
                Console.WriteLine($"Error importing {path}: {ex.Message}");
                return (new VertexPositionNormalBary[0], new ushort[0]);
            }
        }

        // (Optional) Existing STL loader remains available.
        private (VertexPositionNormalBary[] vertices, ushort[] indices) LoadSTLWithBarycentrics(string path)
        {
            var vertexList = new List<VertexPositionNormalBary>();
            var indexList = new List<ushort>();
            try
            {
                using (BinaryReader reader = new BinaryReader(File.OpenRead(path)))
                {
                    reader.ReadBytes(80);
                    int triangleCount = reader.ReadInt32();
                    Console.WriteLine($"Loading {triangleCount} triangles from {Path.GetFileName(path)}");
                    for (int i = 0; i < triangleCount; i++)
                    {
                        Vector3 fileNormal = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                        if (fileNormal.LengthSquared() < 1e-6f)
                            fileNormal = Vector3.UnitY;
                        Vector3 v0 = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                        Vector3 v1 = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                        Vector3 v2 = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                        Vector3 computedNormal = Vector3.Normalize(Vector3.Cross(v1 - v0, v2 - v0));
                        if (Vector3.Dot(computedNormal, fileNormal) < 0)
                        {
                            Vector3 temp = v1;
                            v1 = v2;
                            v2 = temp;
                        }
                        Vector3[] bary = new Vector3[] { new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f) };
                        vertexList.Add(new VertexPositionNormalBary(v0, fileNormal, bary[0]));
                        vertexList.Add(new VertexPositionNormalBary(v1, fileNormal, bary[1]));
                        vertexList.Add(new VertexPositionNormalBary(v2, fileNormal, bary[2]));
                        indexList.Add((ushort)(vertexList.Count - 3));
                        indexList.Add((ushort)(vertexList.Count - 2));
                        indexList.Add((ushort)(vertexList.Count - 1));
                        reader.ReadBytes(2);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading STL file '{path}': {ex.Message}");
                return (new VertexPositionNormalBary[0], new ushort[0]);
            }
            Console.WriteLine($"Loaded {vertexList.Count} vertices from {Path.GetFileName(path)}");
            return (vertexList.ToArray(), indexList.ToArray());
        }

        // Computes a model matrix that centers, scales, and rotates the model (turning it right side up).
        private Matrix4x4 ComputeModelMatrix(VertexPositionNormalBary[] vertices)
        {
            if (vertices.Length == 0)
                return Matrix4x4.Identity;
            Vector3 min = new Vector3(float.MaxValue);
            Vector3 max = new Vector3(float.MinValue);
            foreach (var v in vertices)
            {
                min = Vector3.Min(min, v.Position);
                max = Vector3.Max(max, v.Position);
            }
            Vector3 center = (min + max) / 2;
            float sizeX = max.X - min.X;
            float sizeY = max.Y - min.Y;
            float sizeZ = max.Z - min.Z;
            float modelSize = MathF.Max(sizeX, MathF.Max(sizeY, sizeZ));
            float desiredSize = 2.0f;
            float scaleFactor = desiredSize / modelSize;
            Console.WriteLine($"ComputeModelMatrix: Center={center}, ScaleFactor={scaleFactor}");
            Matrix4x4 translation = Matrix4x4.CreateTranslation(-center);
            Matrix4x4 scale = Matrix4x4.CreateScale(scaleFactor);
            // Rotate -90° about the X-axis to turn the model right side up.
            Matrix4x4 rotation = Matrix4x4.CreateRotationX(-MathF.PI / 2);
            return rotation * (translation * scale);
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
            var pixelFormat = Avalonia.Platform.PixelFormat.Rgb32;
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
                PixelFormat.D32_Float_S8_UInt,
                TextureUsage.DepthStencil));
            _offscreenFramebuffer = factory.CreateFramebuffer(new FramebufferDescription(_offscreenDepthTexture, _offscreenColorTexture));
        }

        private void CreateGridResources()
        {
            ResourceFactory factory = _graphicsDevice.ResourceFactory;
            // Create a larger grid spanning from -40 to +40 and placed at y = -0.1.
            VertexPositionColor[] gridVertices = new VertexPositionColor[]
            {
                new VertexPositionColor(new Vector3(-40f, -0.1f, -40f), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(40f, -0.1f, -40f), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(40f, -0.1f, 40f), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(-40f, -0.1f, 40f), new Vector3(0.5f, 0.5f, 0.5f))
            };

            // Draw the border of the grid.
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

        // Update the MVP using the CameraController.
        private void UpdateMVP()
        {
            _cameraController.Update(0.016f, _input);
            Matrix4x4[] mvp = _cameraController.GetMVPMatrices(_models.Count > 0 ? _models[0].Transform : Matrix4x4.Identity);
            _graphicsDevice.UpdateBuffer(_mvpBuffer, 0, mvp);
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
            _commandList.ClearColorTarget(0, new RgbaFloat(0, 0, 0, 1));  // Clear to black.
            _commandList.ClearDepthStencil(1f);
            _commandList.SetViewport(0, new Viewport(0, 0, (float)Bounds.Width, (float)Bounds.Height, 0, 1));
            _commandList.SetScissorRect(0, 0, 0, (uint)Bounds.Width, (uint)Bounds.Height);

            // Draw grid.
            _commandList.SetPipeline(_gridPipeline);
            _commandList.SetGraphicsResourceSet(0, _mvpResourceSet);
            _commandList.SetVertexBuffer(0, _gridVertexBuffer);
            _commandList.SetIndexBuffer(_gridIndexBuffer, IndexFormat.UInt16);
            _commandList.DrawIndexed((uint)(_gridIndexBuffer.SizeInBytes / sizeof(ushort)), 1, 0, 0, 0);

            // Draw each loaded model.
            foreach (var model in _models)
            {
                _commandList.SetPipeline(_modelPipeline);
                _commandList.SetGraphicsResourceSet(0, _mvpResourceSet);
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

    // Vertex structure with barycentrics.
    public struct VertexPositionNormalBary
    {
        public const uint SizeInBytes = 36; // 3 floats each for Position, Normal, Barycentrics.
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

    // Vertex structure for grid.
    public struct VertexPositionColor
    {
        public const uint SizeInBytes = 24; // 3 floats each for Position and Color.
        public Vector3 Position;
        public Vector3 Color;
        public VertexPositionColor(Vector3 position, Vector3 color)
        {
            Position = position;
            Color = color;
        }
    }

    // Simple model class.
    public class Model
    {
        public DeviceBuffer VertexBuffer { get; set; }
        public DeviceBuffer IndexBuffer { get; set; }
        public int VertexCount { get; set; }
        public int IndexCount { get; set; }
        public Matrix4x4 Transform { get; set; }
    }
}
