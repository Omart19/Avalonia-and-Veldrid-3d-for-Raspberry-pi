using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Input;
using Avalonia.Threading;
using System;
using System.IO;
using System.Numerics;
using System.Text;
using Veldrid;
using Veldrid.SPIRV;
using PixelFormat = Veldrid.PixelFormat;

namespace VeldridSTLViewer
{
    public class VeldridControl : Control
    {
        // Register a Background property so the control is hit-testable.
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
        private DeviceBuffer _vertexBuffer;
        private DeviceBuffer _indexBuffer;

        // We'll use one pipeline for the model that draws fill plus outlines via barycentrics.
        private Shader[] _modelShaders;
        private Pipeline _modelPipeline;

        private DeviceBuffer _mvpBuffer;
        private ResourceLayout _mvpLayout;
        private ResourceSet _mvpResourceSet;
        private Matrix4x4 _modelMatrix;
        private WriteableBitmap _avaloniaBitmap;
        private Framebuffer _offscreenFramebuffer;
        private Texture _offscreenColorTexture;
        private Texture _offscreenDepthTexture;
        private Texture _stagingTexture;
        private bool _resourcesCreated = false;

        private CameraController _cameraController;
        private InputState _input;
        private Avalonia.Point _previousMousePosition;

        // Grid resources.
        private DeviceBuffer _gridVertexBuffer;
        private DeviceBuffer _gridIndexBuffer;
        private Pipeline _gridPipeline;
        private ResourceSet _gridResourceSet;
        private ResourceLayout _gridResourceLayout;
        private Shader[] _gridShaders;
        private Matrix4x4 _gridModelMatrix = Matrix4x4.Identity;

        // --- Shader Code Strings for the model using barycentrics ---
        // The vertex shader passes position, normal, and barycentrics.
        private const string ModelVertexCode = @"
#version 450
layout(location = 0) in vec3 Position;
layout(location = 1) in vec3 Normal;
layout(location = 2) in vec3 Barycentrics;  // New attribute
layout(location = 0) out vec3 v_Bary;
layout(set = 0, binding = 0) uniform MVP {
    mat4 Model;
    mat4 View;
    mat4 Projection;
};
void main()
{
    gl_Position = Projection * View * Model * vec4(Position, 1.0);
    v_Bary = Barycentrics;
}";
        // The fragment shader uses barycentrics to determine edge regions.
        private const string ModelFragmentCode = @"
#version 450
layout(location = 0) in vec3 v_Bary;
layout(location = 0) out vec4 fsout_Color;
void main()
{
    // Near edges, the minimum barycentric coordinate is small.
    float edgeThickness = 0.02; // Adjust this value to change outline width.
    float edgeFactor = 1.0 - smoothstep(0.0, edgeThickness, min(v_Bary.x, min(v_Bary.y, v_Bary.z)));
    // Fill color (dark gray) and outline color (black).
    vec4 fillColor = vec4(0.3, 0.3, 0.3, 1.0);
    vec4 outlineColor = vec4(0.0, 0.0, 0.0, 1.0);
    fsout_Color = mix(fillColor, outlineColor, edgeFactor);
}";
        // Grid shaders (unchanged).
        private const string GridVertexCode = @"
#version 450
layout(location = 0) in vec3 Position;
layout(location = 1) in vec3 Color;
layout(location = 0) out vec3 fsin_Color;
layout(set = 0, binding = 0) uniform MVP
{
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
            if (e != null && _input != null)
            {
                Avalonia.Point currentPosition = e.GetPosition(this);
                Console.WriteLine($"PointerMoved: {currentPosition}");
                _input.MouseDelta = currentPosition - _previousMousePosition;
                _previousMousePosition = currentPosition;
            }
        }
        private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e != null && _input != null)
            {
                Console.WriteLine("PointerPressed");
                _input.SetMouseDown(e.GetCurrentPoint(this).Properties.PointerUpdateKind.GetMouseButton(), true);
                this.Focus();
            }
        }
        private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (e != null && _input != null)
            {
                Console.WriteLine("PointerReleased");
                _input.SetMouseDown(e.GetCurrentPoint(this).Properties.PointerUpdateKind.GetMouseButton(), false);
            }
        }
        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            Console.WriteLine($"KeyDown: {e.Key}");
            _input.SetKeyDown(e.Key, true);
        }
        private void OnKeyUp(object? sender, KeyEventArgs e)
        {
            Console.WriteLine($"KeyUp: {e.Key}");
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

            _offscreenFramebuffer.Dispose();
            _offscreenColorTexture.Dispose();
            _offscreenDepthTexture.Dispose();
            _stagingTexture.Dispose();
            _gridPipeline.Dispose();
            foreach (var shader in _gridShaders)
            {
                shader.Dispose();
            }
            _gridVertexBuffer.Dispose();
            _gridIndexBuffer.Dispose();
            _gridResourceSet.Dispose();
            _gridResourceLayout.Dispose();
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

            // Create the offscreen framebuffer and textures.
            CreateOffscreenFramebuffer();

            // Create the command list.
            _commandList = factory.CreateCommandList();

            // --- Load STL file with barycentrics ---
            var (stlVerts, stlIndices) = LoadSTLWithBarycentrics("model.stl");
            Console.WriteLine($"Loaded {stlVerts.Length} vertices and {stlIndices.Length} indices.");
            if (stlVerts.Length == 0)
            {
                Console.WriteLine("ERROR: STL loading failed. Check model.stl path.");
                return;
            }
            _modelMatrix = ComputeModelMatrix(stlVerts);

            _vertexBuffer = factory.CreateBuffer(new BufferDescription((uint)(stlVerts.Length * VertexPositionNormalBary.SizeInBytes), BufferUsage.VertexBuffer));
            _graphicsDevice.UpdateBuffer(_vertexBuffer, 0, stlVerts);

            _indexBuffer = factory.CreateBuffer(new BufferDescription((uint)(stlIndices.Length * sizeof(ushort)), BufferUsage.IndexBuffer));
            _graphicsDevice.UpdateBuffer(_indexBuffer, 0, stlIndices);
            // --- End Load STL ---

            _mvpBuffer = factory.CreateBuffer(new BufferDescription(3 * 64, BufferUsage.UniformBuffer));
            _mvpLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("MVP", ResourceKind.UniformBuffer, ShaderStages.Vertex)));
            _mvpResourceSet = factory.CreateResourceSet(new ResourceSetDescription(_mvpLayout, _mvpBuffer));

            // Our vertex layout now includes barycentrics.
            VertexLayoutDescription vertexLayout = new VertexLayoutDescription(
                new VertexElementDescription("Position", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
                new VertexElementDescription("Normal", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
                new VertexElementDescription("Barycentrics", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3)
            );

            // Create model shaders and pipeline.
            ShaderDescription modelVertexShaderDesc = new ShaderDescription(ShaderStages.Vertex, Encoding.UTF8.GetBytes(ModelVertexCode), "main");
            ShaderDescription modelFragmentShaderDesc = new ShaderDescription(ShaderStages.Fragment, Encoding.UTF8.GetBytes(ModelFragmentCode), "main");
            _modelShaders = factory.CreateFromSpirv(modelVertexShaderDesc, modelFragmentShaderDesc);
            GraphicsPipelineDescription modelPipelineDesc = new GraphicsPipelineDescription
            {
                BlendState = BlendStateDescription.SingleOverrideBlend,
                RasterizerState = new RasterizerStateDescription(
                    cullMode: FaceCullMode.Back,
                    fillMode: PolygonFillMode.Solid,
                    frontFace: FrontFace.CounterClockwise,
                    depthClipEnabled: true,
                    scissorTestEnabled: false),
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
            _cameraController.SetCameraPosition(new Vector3(2, 2, 2));
            _cameraController.SetCameraRotation(0, 0);

            _resourcesCreated = true;
        }

        // New method: load STL file and assign barycentrics for each triangle.
        private (VertexPositionNormalBary[] vertices, ushort[] indices) LoadSTLWithBarycentrics(string path)
        {
            var vertexList = new System.Collections.Generic.List<VertexPositionNormalBary>();
            var indexList = new System.Collections.Generic.List<ushort>();
            try
            {
                using (BinaryReader reader = new BinaryReader(File.OpenRead(path)))
                {
                    reader.ReadBytes(80); // Skip header.
                    int triangleCount = reader.ReadInt32();
                    for (int i = 0; i < triangleCount; i++)
                    {
                        Vector3 normal = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                        if (normal.LengthSquared() < 0.0001f)
                        {
                            Console.WriteLine($"Warning: Triangle {i} has near-zero normal: {normal}");
                            normal = Vector3.UnitY;
                        }
                        // Assign barycentrics: first vertex: (1,0,0), second: (0,1,0), third: (0,0,1)
                        Vector3[] bary = new Vector3[] { new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1) };
                        for (int j = 0; j < 3; j++)
                        {
                            Vector3 pos = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                            vertexList.Add(new VertexPositionNormalBary(pos, normal, bary[j]));
                            indexList.Add((ushort)(vertexList.Count - 1));
                        }
                        reader.ReadBytes(2); // Skip attribute count.
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading STL file: {ex.Message}");
                return (new VertexPositionNormalBary[0], new ushort[0]);
            }
            Console.WriteLine($"Loaded Vertices count: {vertexList.Count}");
            return (vertexList.ToArray(), indexList.ToArray());
        }

        private void CreateOffscreenFramebuffer()
        {
            if (_graphicsDevice == null) return;
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

        private void CreateStagingTexture()
        {
            if (_graphicsDevice == null) return;
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
            if (_graphicsDevice == null) return;
            var pixelFormat = Avalonia.Platform.PixelFormat.Rgb32;
            var alphaFormat = Avalonia.Platform.AlphaFormat.Premul;
            _avaloniaBitmap = new WriteableBitmap(
                new Avalonia.PixelSize((int)Math.Max(1, Bounds.Width), (int)Math.Max(1, Bounds.Height)),
                new Avalonia.Vector(96, 96),
                pixelFormat,
                alphaFormat);
        }

        private void UpdateMVP()
        {
            _cameraController.Update(0.016f, _input);
            _graphicsDevice.UpdateBuffer(_mvpBuffer, 0, _cameraController.GetMVPMatrices(_modelMatrix));
        }

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
            float modelSize = MathF.Max(max.X - min.X, MathF.Max(max.Y - min.Y, max.Z - min.Z));
            float desiredSize = 2.0f;
            float scaleFactor = desiredSize / modelSize;
            Console.WriteLine($"Scale Factor Before: {scaleFactor}");
            if (float.IsNaN(scaleFactor) || float.IsInfinity(scaleFactor))
            {
                scaleFactor = 1.0f;
                Console.WriteLine("Scale factor was NaN or Infinity. Setting to 1.0");
            }
            Console.WriteLine($"Scale Factor After: {scaleFactor}");
            Matrix4x4 translation = Matrix4x4.CreateTranslation(-center);
            Matrix4x4 scale = Matrix4x4.CreateScale(scaleFactor);
            Console.WriteLine($"ComputeModelMatrix - Min: {min}, Max: {max}, Center: {center}, ScaleFactor: {scaleFactor}");
            Matrix4x4 result = translation * scale;
            Console.WriteLine($"Result Matrix: {result}");
            return result;
        }

        public override void Render(Avalonia.Media.DrawingContext context)
        {
            if (!_resourcesCreated)
                return;

            Draw();

            Dispatcher.UIThread.InvokeAsync(() =>
            {
                using (var lockedBitmap = _avaloniaBitmap.Lock())
                {
                    MappedResourceView<byte> map = _graphicsDevice.Map<byte>(_stagingTexture, MapMode.Read);
                    try
                    {
                        unsafe
                        {
                            Buffer.MemoryCopy(
                                (void*)map.MappedResource.Data,
                                (void*)lockedBitmap.Address,
                                (int)map.MappedResource.SizeInBytes,
                                (int)map.MappedResource.SizeInBytes);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex.ToString());
                    }
                    _graphicsDevice.Unmap(_stagingTexture);
                }
                context.DrawImage(_avaloniaBitmap, new Rect(0, 0, Bounds.Width, Bounds.Height));
                base.Render(context);
            }, DispatcherPriority.Render);

            Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background);
            _input.ClearDelta();
        }

        private void Draw()
        {
            if (_graphicsDevice == null)
                return;

            UpdateMVP();

            _commandList.Begin();
            _commandList.SetFramebuffer(_offscreenFramebuffer);
            _commandList.ClearColorTarget(0, RgbaFloat.Black);
            _commandList.ClearDepthStencil(1f);
            _commandList.SetViewport(0, new Viewport(0, 0, (float)Bounds.Width, (float)Bounds.Height, 0, 1));
            _commandList.SetScissorRect(0, 0, 0, (uint)Bounds.Width, (uint)Bounds.Height);

            // Draw grid.
            _commandList.SetPipeline(_gridPipeline);
            _commandList.SetGraphicsResourceSet(0, _gridResourceSet);
            _commandList.SetVertexBuffer(0, _gridVertexBuffer);
            _commandList.SetIndexBuffer(_gridIndexBuffer, IndexFormat.UInt16);
            _commandList.DrawIndexed((uint)_gridIndexBuffer.SizeInBytes / sizeof(ushort), 1, 0, 0, 0);

            // Draw the model with fill and outlines (via barycentrics).
            _commandList.SetPipeline(_modelPipeline);
            _commandList.SetGraphicsResourceSet(0, _mvpResourceSet);
            _commandList.SetVertexBuffer(0, _vertexBuffer);
            _commandList.SetIndexBuffer(_indexBuffer, IndexFormat.UInt16);
            _commandList.DrawIndexed(
                indexCount: (uint)(_indexBuffer.SizeInBytes / sizeof(ushort)),
                instanceCount: 1,
                indexStart: 0,
                vertexOffset: 0,
                instanceStart: 0);

            _commandList.CopyTexture(_offscreenColorTexture, _stagingTexture);
            _commandList.End();
            _graphicsDevice.SubmitCommands(_commandList);
        }

        private void CreateGridResources()
        {
            ResourceFactory factory = _graphicsDevice.ResourceFactory;

            VertexPositionColor[] gridVertices = new VertexPositionColor[]
            {
                // Horizontal lines (XZ plane)
                new VertexPositionColor(new Vector3(-10, 0, -10), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(10, 0, -10), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(-10, 0, -8), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(10, 0, -8), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(-10, 0, -6), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(10, 0, -6), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(-10, 0, -4), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(10, 0, -4), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(-10, 0, -2), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(10, 0, -2), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(-10, 0, 0), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(10, 0, 0), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(-10, 0, 2), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(10, 0, 2), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(-10, 0, 4), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(10, 0, 4), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(-10, 0, 6), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(10, 0, 6), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(-10, 0, 8), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(10, 0, 8), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(-10, 0, 10), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(10, 0, 10), new Vector3(0.5f, 0.5f, 0.5f)),

                // Vertical lines (constant X, varying Z)
                new VertexPositionColor(new Vector3(-10, 0, -10), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(-10, 0, 10), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(-8, 0, -10), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(-8, 0, 10), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(-6, 0, -10), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(-6, 0, 10), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(-4, 0, -10), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(-4, 0, 10), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(-2, 0, -10), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(-2, 0, 10), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(0, 0, -10), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(0, 0, 10), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(2, 0, -10), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(2, 0, 10), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(4, 0, -10), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(4, 0, 10), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(6, 0, -10), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(6, 0, 10), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(8, 0, -10), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(8, 0, 10), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(10, 0, -10), new Vector3(0.5f, 0.5f, 0.5f)),
                new VertexPositionColor(new Vector3(10, 0, 10), new Vector3(0.5f, 0.5f, 0.5f))
            };

            ushort[] gridIndices = new ushort[]
            {
                0, 1,  2, 3,  4, 5,  6, 7,  8, 9, 10, 11,
                12, 13, 14, 15, 16, 17, 18, 19, 20, 21,
                22, 23, 24, 25, 26, 27, 28, 29, 30, 31,
                32, 33, 34, 35, 36, 37, 38, 39, 40, 41
            };

            _gridVertexBuffer = factory.CreateBuffer(new BufferDescription((uint)(gridVertices.Length * VertexPositionColor.SizeInBytes), BufferUsage.VertexBuffer));
            _graphicsDevice.UpdateBuffer(_gridVertexBuffer, 0, gridVertices);

            _gridIndexBuffer = factory.CreateBuffer(new BufferDescription((uint)(gridIndices.Length * sizeof(ushort)), BufferUsage.IndexBuffer));
            _graphicsDevice.UpdateBuffer(_gridIndexBuffer, 0, gridIndices);

            ShaderDescription gridVertexShaderDesc = new ShaderDescription(ShaderStages.Vertex, Encoding.UTF8.GetBytes(GridVertexCode), "main");
            ShaderDescription gridFragmentShaderDesc = new ShaderDescription(ShaderStages.Fragment, Encoding.UTF8.GetBytes(GridFragmentCode), "main");
            _gridShaders = factory.CreateFromSpirv(gridVertexShaderDesc, gridFragmentShaderDesc);

            VertexLayoutDescription gridVertexLayout = new VertexLayoutDescription(
                new VertexElementDescription("Position", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
                new VertexElementDescription("Color", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3)
            );

            _gridResourceLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("MVP", ResourceKind.UniformBuffer, ShaderStages.Vertex)));

            _gridResourceSet = factory.CreateResourceSet(new ResourceSetDescription(_gridResourceLayout, _mvpBuffer));

            GraphicsPipelineDescription gridPipelineDescription = new GraphicsPipelineDescription
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

            _gridPipeline = factory.CreateGraphicsPipeline(gridPipelineDescription);
        }

        private void DisposeResources()
        {
            _resourcesCreated = false;

            _modelPipeline?.Dispose();

            if (_modelShaders != null)
            {
                foreach (Shader shader in _modelShaders)
                    shader.Dispose();
            }
            _commandList?.Dispose();
            _vertexBuffer?.Dispose();
            _indexBuffer?.Dispose();
            _mvpBuffer?.Dispose();
            _mvpResourceSet?.Dispose();
            _mvpLayout?.Dispose();
            _offscreenFramebuffer?.Dispose();
            _offscreenColorTexture?.Dispose();
            _offscreenDepthTexture?.Dispose();
            _stagingTexture?.Dispose();

            _gridPipeline.Dispose();
            foreach (var shader in _gridShaders)
                shader.Dispose();
            _gridVertexBuffer.Dispose();
            _gridIndexBuffer.Dispose();
            _gridResourceSet.Dispose();
            _gridResourceLayout.Dispose();

            _graphicsDevice?.Dispose();
            _avaloniaBitmap = null;
        }
    }

    // New vertex structure that includes barycentrics.
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

    // For grid vertices.
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
}
