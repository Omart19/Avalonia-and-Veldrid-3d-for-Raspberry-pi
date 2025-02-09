using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Veldrid;
using Veldrid.SPIRV;
using Avalonia.Media.Imaging;
using PixelFormat = Veldrid.PixelFormat;
using Bitmap = Avalonia.Media.Imaging.Bitmap;
using Avalonia.Interactivity;
using Avalonia.Input; // For RoutedEventArgs

namespace VeldridSTLViewer
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
            var mainPanel = this.FindControl<StackPanel>("MainPanel");
            // Embed VeldridControl inside a Border:
            var border = new Border();
            var veldridControl = new VeldridControl();
            veldridControl.Width = 960;  // SET EXPLICIT SIZE
            veldridControl.Height = 540; // SET EXPLICIT SIZE
            veldridControl.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch; // Stretch
            veldridControl.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;   // Stretch
            border.Child = veldridControl;
            mainPanel.Children.Add(border);
        }

    }

    public class VeldridControl : Control
    {
        private GraphicsDevice _graphicsDevice;
        private CommandList _commandList;
        private DeviceBuffer _vertexBuffer;
        private DeviceBuffer _indexBuffer;
        private Shader[] _shaders;
        private Pipeline _pipeline;
        private DeviceBuffer _mvpBuffer;
        private ResourceLayout _mvpLayout;
        private Veldrid.ResourceSet _mvpResourceSet; // Explicit namespace
        private Matrix4x4 _modelMatrix;
        private WriteableBitmap _avaloniaBitmap;
        private Framebuffer _offscreenFramebuffer;
        private Texture _offscreenColorTexture;
        private Texture _offscreenDepthTexture;
        private Texture _stagingTexture;
        private bool _resourcesCreated = false;
        private int _renderCount = 0; // Counter for Render calls
        private CameraController _cameraController;
        private InputState _input; // Add this for input handling
                                   // Add these fields to your VeldridControl class:
        private DeviceBuffer _gridVertexBuffer;
        private DeviceBuffer _gridIndexBuffer;
        private Pipeline _gridPipeline;
        private ResourceSet _gridResourceSet;
        private ResourceLayout _gridResourceLayout;
        private Shader[] _gridShaders;

        // Add these constants for the grid shaders:
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
    fsout_Color = vec4(fsin_Color, 1.0); // Use the color directly
}";

        // Simplified shaders
        private const string VertexCode = @"
        #version 450
        layout(location = 0) in vec3 Position;
        layout(location = 1) in vec3 Normal;
        layout(location = 0) out vec3 v_Normal;
        layout(set = 0, binding = 0) uniform MVP
        {
            mat4 Model;
            mat4 View;
            mat4 Projection;
        };
        void main()
        {
            gl_Position = Projection * View * Model * vec4(Position, 1.0);
            v_Normal = mat3(Model) * Normal;
        }";

        // Use the original fragment shader with lighting
        private const string FragmentCode = @"
#version 450
layout(location = 0) in vec3 v_Normal;
layout(location = 0) out vec4 fsout_Color;
void main()
{
vec3 lightDir = normalize(vec3(0.0, 0.0, 1.0));  // Light from the front (along -Z)
    float brightness = abs(dot(normalize(v_Normal), lightDir)); // ABSOLUTE VALUE of dot product
    fsout_Color = vec4(vec3(brightness), 1.0);
}
";

        public VeldridControl()
        {
            this.Focusable = true; // Make sure the control can receive keyboard input.
            this.AttachedToVisualTree += OnAttachedToVisualTree;
            this.DetachedFromVisualTree += OnDetachedFromVisualTree;

            // Mouse and keyboard event handlers:
            this.PointerMoved += OnPointerMoved;
            this.PointerPressed += OnPointerPressed;
            this.PointerReleased += OnPointerReleased;
            this.KeyDown += OnKeyDown;
            this.KeyUp += OnKeyUp;
        }
        private void OnLoaded(object? sender, RoutedEventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                this.InvalidateMeasure();
                this.InvalidateArrange();
                this.Measure(this.Bounds.Size);
                this.Arrange(new Rect(this.Bounds.Size));
            }, DispatcherPriority.Render); // Or DispatcherPriority.Loaded
        }

        private void OnAttachedToVisualTree(object sender, VisualTreeAttachmentEventArgs e)
        {
            InitializeVeldrid();

            // Force a layout update *after* Veldrid is initialized and the control is attached:
            Dispatcher.UIThread.Post(() =>
            {
                this.InvalidateMeasure();
                this.InvalidateArrange();
                this.Measure(this.Bounds.Size);
                this.Arrange(new Rect(this.Bounds.Size));
            }, DispatcherPriority.Loaded); // Use Loaded priority
        }

        private void OnDetachedFromVisualTree(object sender, VisualTreeAttachmentEventArgs e)
        {
            DisposeResources();
        }

        private void InitializeVeldrid()
        {
            try
            {
                GraphicsDeviceOptions options = new GraphicsDeviceOptions
                {
                    PreferStandardClipSpaceYDirection = true,
                    PreferDepthRangeZeroToOne = true,
                    SyncToVerticalBlank = false, //VSync off is important
                    ResourceBindingModel = ResourceBindingModel.Improved,
                    SwapchainDepthFormat = PixelFormat.D24_UNorm_S8_UInt,
                    Debug = true // Enable Veldrid debug mode
                };

                _graphicsDevice = GraphicsDevice.CreateVulkan(options); // Stick with Vulkan
                CreateResources();

            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error initializing Veldrid: {ex}");
            }
        }

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            Console.WriteLine($"OnSizeChanged: NewSize = {e.NewSize}, Bounds = {this.Bounds}"); // Check size
            if (_resourcesCreated && e.NewSize.Width > 0 && e.NewSize.Height > 0)
            {
                ResizeResources();
            }
        }
        // Add these input event handler methods:
        private void OnPointerMoved(object? sender, PointerEventArgs e)
        {
            _input.UpdateMousePosition(e.GetPosition(this));
        }

        private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            _input.SetMouseDown(e.GetCurrentPoint(this).Properties.PointerUpdateKind.GetMouseButton(), true);
            this.Focus(); // VERY IMPORTANT: Request focus when clicked
        }

        private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            _input.SetMouseDown(e.GetCurrentPoint(this).Properties.PointerUpdateKind.GetMouseButton(), false);
        }
        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            _input.SetKeyDown(e.Key, true);
        }

        private void OnKeyUp(object? sender, KeyEventArgs e)
        {
            _input.SetKeyDown(e.Key, false);
        }
        private void ResizeResources()
        {
            if (_graphicsDevice == null) return;

            // Wait for all GPU operations to complete before resizing.  ESSENTIAL.
            _graphicsDevice.WaitForIdle();

            // Dispose old resources.
            _offscreenFramebuffer.Dispose();
            _offscreenColorTexture.Dispose();
            _offscreenDepthTexture.Dispose();
            _stagingTexture.Dispose();
            // Dispose of the grid resources
            _gridPipeline.Dispose();
            foreach (var shader in _gridShaders)
            {
                shader.Dispose();
            }
            _gridVertexBuffer.Dispose();
            _gridIndexBuffer.Dispose();
            _gridResourceSet.Dispose();
            _gridResourceLayout.Dispose();
            _avaloniaBitmap?.Dispose(); _avaloniaBitmap = null;

            // Recreate size-dependent resources.
            CreateOffscreenFramebuffer();
            CreateStagingTexture();
            CreateAvaloniaBitmap();
            CreateGridResources();
            _cameraController.UpdateAspectRatio((float)Bounds.Width / (float)Bounds.Height); // Update aspect ratio
            Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background);

        }


        private void CreateResources()
        {
            if (_graphicsDevice == null) return;

            ResourceFactory factory = _graphicsDevice.ResourceFactory;

            // --- Load STL --- (Keep this as it is)
            var (stlVertices, stlIndices) = LoadSTL("model.stl");
            Console.WriteLine($"Loaded {stlVertices.Length} vertices and {stlIndices.Length} indices.");

            if (stlVertices.Length == 0)
            {
                Console.WriteLine("ERROR: STL loading failed.  Check model.stl path.");
                return;
            }

            _modelMatrix = ComputeModelMatrix(stlVertices);

            _vertexBuffer = factory.CreateBuffer(new BufferDescription((uint)(stlVertices.Length * VertexPositionNormal.SizeInBytes), BufferUsage.VertexBuffer));
            _graphicsDevice.UpdateBuffer(_vertexBuffer, 0, stlVertices);

            _indexBuffer = factory.CreateBuffer(new BufferDescription((uint)(stlIndices.Length * sizeof(ushort)), BufferUsage.IndexBuffer));
            _graphicsDevice.UpdateBuffer(_indexBuffer, 0, stlIndices);
            // --- End Load STL ---

            _mvpBuffer = factory.CreateBuffer(new BufferDescription(3 * 64, BufferUsage.UniformBuffer));
            _mvpLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("MVP", ResourceKind.UniformBuffer, ShaderStages.Vertex)));
            _mvpResourceSet = factory.CreateResourceSet(new ResourceSetDescription(_mvpLayout, _mvpBuffer));

            VertexLayoutDescription vertexLayout = new VertexLayoutDescription(
                new VertexElementDescription("Position", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
                new VertexElementDescription("Normal", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3)
            );

            ShaderDescription vertexShaderDesc = new ShaderDescription(ShaderStages.Vertex, Encoding.UTF8.GetBytes(VertexCode), "main");
            ShaderDescription fragmentShaderDesc = new ShaderDescription(ShaderStages.Fragment, Encoding.UTF8.GetBytes(FragmentCode), "main");
            _shaders = factory.CreateFromSpirv(vertexShaderDesc, fragmentShaderDesc);

            CreateOffscreenFramebuffer();
            CreateStagingTexture();

            GraphicsPipelineDescription pipelineDescription = new GraphicsPipelineDescription
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
                ShaderSet = new ShaderSetDescription(new VertexLayoutDescription[] { vertexLayout }, _shaders),
                Outputs = _offscreenFramebuffer.OutputDescription
            };
            _pipeline = factory.CreateGraphicsPipeline(pipelineDescription);
            _commandList = factory.CreateCommandList();

            CreateAvaloniaBitmap();

            // --- CAMERA CONTROLLER INITIALIZATION ---
            _cameraController = new CameraController((float)this.Bounds.Width / (float)this.Bounds.Height);
            _input = new InputState(); // Create instance of InputState
            _cameraController.SetCameraPosition(new Vector3(2, 2, 2));  // Example position
            _cameraController.SetCameraRotation(0, 0); //set initial rotation
            CreateGridResources();

            _resourcesCreated = true;
            Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background);
        }
        private void CreateOffscreenFramebuffer()
        {
            if (_graphicsDevice == null) return; // Important guard
            ResourceFactory factory = _graphicsDevice.ResourceFactory;
            _offscreenColorTexture = factory.CreateTexture(TextureDescription.Texture2D(
               (uint)Math.Max(1, Bounds.Width), (uint)Math.Max(1, Bounds.Height), 1, 1,
               PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.RenderTarget | TextureUsage.Sampled));
            _offscreenColorTexture.Name = "OffscreenColorTexture"; // Give it a name!

            _offscreenDepthTexture = factory.CreateTexture(TextureDescription.Texture2D(
                (uint)Math.Max(1, Bounds.Width), (uint)Math.Max(1, Bounds.Height), 1, 1,
                PixelFormat.D24_UNorm_S8_UInt, TextureUsage.DepthStencil));  // Use a standard depth format
            _offscreenDepthTexture.Name = "OffscreenDepthTexture";  // Give it a name!

            _offscreenFramebuffer = factory.CreateFramebuffer(new FramebufferDescription(_offscreenDepthTexture, _offscreenColorTexture));
            _offscreenFramebuffer.Name = "OffscreenFramebuffer";
            // *** VERIFY FRAMEBUFFER ATTACHMENTS ***
            Console.WriteLine("Framebuffer Created:");
            Console.WriteLine($"  Color Target: {_offscreenFramebuffer.ColorTargets[0].Target.Name ?? "Unnamed"}");
            Console.WriteLine($"  Depth Target: {_offscreenFramebuffer.DepthTarget?.Target.Name ?? "Unnamed"}");
        }
        private void CreateStagingTexture()
        {
            if (_graphicsDevice == null) return;
            ResourceFactory factory = _graphicsDevice.ResourceFactory;
            _stagingTexture = factory.CreateTexture(TextureDescription.Texture2D(
                (uint)Math.Max(1, Bounds.Width), (uint)Math.Max(1, Bounds.Height), 1, 1,
                PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Staging));
            _stagingTexture.Name = "StagingTexture"; // Add a name here
        }
        private void CreateAvaloniaBitmap()
        {
            if (_graphicsDevice == null) return;
            var pixelFormat = Avalonia.Platform.PixelFormat.Rgba8888; // Explicitly define
            var alphaFormat = Avalonia.Platform.AlphaFormat.Unpremul;

            Console.WriteLine($"Avalonia Bitmap Pixel Format: {pixelFormat}"); // Check

            _avaloniaBitmap = new WriteableBitmap(
                new Avalonia.PixelSize((int)Math.Max(1, Bounds.Width), (int)Math.Max(1, Bounds.Height)),
                new Avalonia.Vector(96, 96),
                pixelFormat, // Use defined format
                alphaFormat);
        }
        //Vector3 cameraPosition = new Vector3(0, 0, 5);

        private void UpdateMVP()
        {
            _cameraController.Update(0.016f, _input);
            var mvpMatrices = _cameraController.GetMVPMatrices(_modelMatrix);
            _graphicsDevice.UpdateBuffer(_mvpBuffer, 0, mvpMatrices);
        }
        private Matrix4x4 ComputeModelMatrix(VertexPositionNormal[] vertices)
        {
            if (vertices.Length == 0)
            {
                return Matrix4x4.Identity;
            }
            Vector3 min = new Vector3(float.MaxValue);
            Vector3 max = new Vector3(float.MinValue);
            foreach (var v in vertices)
            {
                min = Vector3.Min(min, v.Position);
                max = Vector3.Max(max, v.Position);
            }
            Vector3 center = (min + max) / 2;
            float modelSize = Vector3.Distance(min, max);
            float desiredSize = 2.0f;
            float scaleFactor = desiredSize / modelSize;
            Console.WriteLine($"Scale Factor Before: {scaleFactor}");
            //Prevent the scale from being zero or infinity.
            if (float.IsNaN(scaleFactor) || float.IsInfinity(scaleFactor))
            {
                scaleFactor = 1.0f;
                Console.WriteLine("Scale factor was NaN or Infinity. Setting to 1.0");
            }
            Console.WriteLine($"Scale Factor After: {scaleFactor}");
            Matrix4x4 translation = Matrix4x4.CreateTranslation(-center);
            Matrix4x4 scale = Matrix4x4.CreateScale(scaleFactor);
            Console.WriteLine($"ComputeModelMatrix - Min: {min}, Max: {max}, Center: {center}, ScaleFactor: {scaleFactor}"); // Keep this
            var result = translation * scale;

            Console.WriteLine($"Result Matrix: {result}");
            return result;
        }

        private (VertexPositionNormal[], ushort[]) LoadSTL(string path)
        {
            var vertices = new System.Collections.Generic.List<VertexPositionNormal>();
            var indices = new System.Collections.Generic.List<ushort>();
            try
            {
                using (BinaryReader reader = new BinaryReader(File.OpenRead(path)))
                {
                    reader.ReadBytes(80); // Skip STL header.
                    int triangleCount = reader.ReadInt32();
                    for (int i = 0; i < triangleCount; i++)
                    {
                        Vector3 normal = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

                        // *** CHECK FOR ZERO-LENGTH NORMALS AND PRINT ***
                        if (normal.LengthSquared() < 0.0001f)
                        {
                            Console.WriteLine($"Warning: Triangle {i} has a near-zero normal: {normal}");
                            //Optionally, we can skip this triangle.
                            normal = Vector3.UnitY;
                        }
                        Console.WriteLine($"Normal: {normal}"); // Print every normal

                        for (int j = 0; j < 3; j++)
                        {
                            Vector3 position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                            vertices.Add(new VertexPositionNormal(position, normal));
                            indices.Add((ushort)(vertices.Count - 1));
                        }
                        reader.ReadBytes(2); // Skip attribute byte count.
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading STL file: {ex.Message}");
                return (new VertexPositionNormal[0], new ushort[0]); // Return empty arrays
            }
            Console.WriteLine($"Loaded Vertices count: {vertices.Count}");
            return (vertices.ToArray(), indices.ToArray());
        }
        public override void Render(Avalonia.Media.DrawingContext context)
        {
            _renderCount++;
            Console.WriteLine($"Render called. Count: {_renderCount}"); // Check if Render is called

            if (!_resourcesCreated) return;

            Draw(); // Render and copy to staging texture

            // Use Dispatcher.InvokeAsync for synchronization:
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                using (var lockedBitmap = _avaloniaBitmap.Lock())
                {
                    MappedResourceView<byte> map = _graphicsDevice.Map<byte>(_stagingTexture, MapMode.Read);
                    byte[] pixelData = new byte[(int)map.MappedResource.SizeInBytes];
                    Marshal.Copy(map.MappedResource.Data, pixelData, 0, (int)map.MappedResource.SizeInBytes);
                    Marshal.Copy(pixelData, 0, lockedBitmap.Address, (int)map.MappedResource.SizeInBytes);
                    _graphicsDevice.Unmap(_stagingTexture);
                }

                context.DrawImage(_avaloniaBitmap, new Rect(0, 0, Bounds.Width, Bounds.Height));
                base.Render(context); // Call base.Render
            }, DispatcherPriority.Render); // Highest priority

            // Invalidate visual AFTER the copy:
            Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background);
        }

        private void Draw()
        {
            if (_graphicsDevice == null) return;

            UpdateMVP(); // Update camera and model matrices

            _commandList.Begin();
            _commandList.SetFramebuffer(_offscreenFramebuffer);
            _commandList.ClearColorTarget(0, RgbaFloat.Black); // Clear to black
            _commandList.ClearDepthStencil(1f);

            // Viewport and Scissor - Keep these as they are, they are correct
            //Console.WriteLine($"Draw - Bounds: {Bounds}"); // Verify bounds
            _commandList.SetViewport(0, new Viewport(0, 0, (float)Bounds.Width, (float)Bounds.Height, 0, 1));
            _commandList.SetScissorRect(0, 0, 0, (uint)Bounds.Width, (uint)Bounds.Height);

            // Draw Grid (Keep this as it is)
            _commandList.SetPipeline(_gridPipeline);
            _commandList.SetGraphicsResourceSet(0, _gridResourceSet);
            _commandList.SetVertexBuffer(0, _gridVertexBuffer);
            _commandList.SetIndexBuffer(_gridIndexBuffer, IndexFormat.UInt16);
            _commandList.DrawIndexed((uint)_gridIndexBuffer.SizeInBytes / sizeof(ushort), 1, 0, 0, 0);

            // Draw Model
            _commandList.SetPipeline(_pipeline);
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
            // _graphicsDevice.WaitForIdle(); // REMOVED - Use Dispatcher.InvokeAsync
        }
        private void CreateGridResources()
        {
            ResourceFactory factory = _graphicsDevice.ResourceFactory;

            // Define grid vertices (XZ plane)
            // Define grid vertices (XZ plane) - More lines for a denser grid
            VertexPositionColor[] gridVertices = new VertexPositionColor[]
            {
        //Horizontal lines
        new VertexPositionColor(new Vector3(-10, 0, -10), new Vector3(0.5f, 0.5f, 0.5f)),
        new VertexPositionColor(new Vector3( 10, 0, -10), new Vector3(0.5f, 0.5f, 0.5f)),

        new VertexPositionColor(new Vector3(-10, 0, -8), new Vector3(0.5f, 0.5f, 0.5f)),
        new VertexPositionColor(new Vector3( 10, 0, -8), new Vector3(0.5f, 0.5f, 0.5f)),

         new VertexPositionColor(new Vector3(-10, 0, -6), new Vector3(0.5f, 0.5f, 0.5f)),
        new VertexPositionColor(new Vector3( 10, 0, -6), new Vector3(0.5f, 0.5f, 0.5f)),

         new VertexPositionColor(new Vector3(-10, 0, -4), new Vector3(0.5f, 0.5f, 0.5f)),
        new VertexPositionColor(new Vector3( 10, 0, -4), new Vector3(0.5f, 0.5f, 0.5f)),

        new VertexPositionColor(new Vector3(-10, 0, -2), new Vector3(0.5f, 0.5f, 0.5f)),
        new VertexPositionColor(new Vector3( 10, 0, -2), new Vector3(0.5f, 0.5f, 0.5f)),

        new VertexPositionColor(new Vector3(-10, 0, 0), new Vector3(0.5f, 0.5f, 0.5f)),
        new VertexPositionColor(new Vector3( 10, 0, 0), new Vector3(0.5f, 0.5f, 0.5f)),

        new VertexPositionColor(new Vector3(-10, 0, 2), new Vector3(0.5f, 0.5f, 0.5f)),
        new VertexPositionColor(new Vector3( 10, 0, 2), new Vector3(0.5f, 0.5f, 0.5f)),

         new VertexPositionColor(new Vector3(-10, 0, 4), new Vector3(0.5f, 0.5f, 0.5f)),
        new VertexPositionColor(new Vector3( 10, 0, 4), new Vector3(0.5f, 0.5f, 0.5f)),

        new VertexPositionColor(new Vector3(-10, 0, 6), new Vector3(0.5f, 0.5f, 0.5f)),
        new VertexPositionColor(new Vector3( 10, 0, 6), new Vector3(0.5f, 0.5f, 0.5f)),

        new VertexPositionColor(new Vector3(-10, 0, 8), new Vector3(0.5f, 0.5f, 0.5f)),
        new VertexPositionColor(new Vector3( 10, 0, 8), new Vector3(0.5f, 0.5f, 0.5f)),

        new VertexPositionColor(new Vector3(-10, 0, 10), new Vector3(0.5f, 0.5f, 0.5f)),
        new VertexPositionColor(new Vector3( 10, 0, 10), new Vector3(0.5f, 0.5f, 0.5f)),

        //Vertical lines
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
            new VertexPositionColor(new Vector3(10, 0,  10), new Vector3(0.5f, 0.5f, 0.5f)),
        };

            //More indices for more lines.
            ushort[] gridIndices = new ushort[] { 0,1, 2,3, 4,5, 6,7, 8,9, 10,11, 12,13, 14,15, 16,17, 18,19, 20,21,
                                             22,23, 24,25, 26,27, 28,29, 30,31, 32,33, 34,35, 36,37, 38,39, 40,41 };

            _gridVertexBuffer = factory.CreateBuffer(new BufferDescription((uint)(gridVertices.Length * VertexPositionColor.SizeInBytes), BufferUsage.VertexBuffer));
            _graphicsDevice.UpdateBuffer(_gridVertexBuffer, 0, gridVertices);

            _gridIndexBuffer = factory.CreateBuffer(new BufferDescription((uint)(gridIndices.Length * sizeof(ushort)), BufferUsage.IndexBuffer));
            _graphicsDevice.UpdateBuffer(_gridIndexBuffer, 0, gridIndices);

            // Shaders for the grid (simplified - no lighting, just color)

            ShaderDescription gridVertexShaderDesc = new ShaderDescription(ShaderStages.Vertex, Encoding.UTF8.GetBytes(GridVertexCode), "main");
            ShaderDescription gridFragmentShaderDesc = new ShaderDescription(ShaderStages.Fragment, Encoding.UTF8.GetBytes(GridFragmentCode), "main");
            _gridShaders = factory.CreateFromSpirv(gridVertexShaderDesc, gridFragmentShaderDesc);


            // Vertex layout for grid (position + color)
            VertexLayoutDescription gridVertexLayout = new VertexLayoutDescription(
                new VertexElementDescription("Position", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
                new VertexElementDescription("Color", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3) // Color
            );

            // Resource layout and set for the grid (use the same MVP layout)
            _gridResourceLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
            new ResourceLayoutElementDescription("MVP", ResourceKind.UniformBuffer, ShaderStages.Vertex)));

            _gridResourceSet = factory.CreateResourceSet(new ResourceSetDescription(_gridResourceLayout, _mvpBuffer)); // Reuse MVP buffer


            // Pipeline for the grid
            GraphicsPipelineDescription gridPipelineDescription = new GraphicsPipelineDescription
            {
                BlendState = BlendStateDescription.SingleOverrideBlend,
                RasterizerState = new RasterizerStateDescription(
                    cullMode: FaceCullMode.None, // Don't cull for the grid
                    fillMode: PolygonFillMode.Solid,  //  can change to Wireframe
                    frontFace: FrontFace.CounterClockwise,
                    depthClipEnabled: true,
                    scissorTestEnabled: false),
                PrimitiveTopology = PrimitiveTopology.LineStrip, // Use LineStrip for grid
                ResourceLayouts = new ResourceLayout[] { _gridResourceLayout }, // Grid layout
                ShaderSet = new ShaderSetDescription(new VertexLayoutDescription[] { gridVertexLayout }, _gridShaders),
                Outputs = _offscreenFramebuffer.OutputDescription
            };
            _gridPipeline = factory.CreateGraphicsPipeline(gridPipelineDescription);
        }


        private void DisposeResources()
        {
            _resourcesCreated = false;

            _pipeline?.Dispose();
            if (_shaders != null)
            {
                foreach (Shader shader in _shaders)
                {
                    shader.Dispose();
                }
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
            // Dispose of the grid resources
            _gridPipeline.Dispose();
            foreach (var shader in _gridShaders)
            {
                shader.Dispose();
            }
            _gridVertexBuffer.Dispose();
            _gridIndexBuffer.Dispose();
            _gridResourceSet.Dispose();
            _gridResourceLayout.Dispose();

            _graphicsDevice?.Dispose();
            _avaloniaBitmap = null;
        }
    }
    struct VertexPositionNormal
    {
        public const uint SizeInBytes = 24;
        public Vector3 Position;
        public Vector3 Normal;

        public VertexPositionNormal(Vector3 position, Vector3 normal)
        {
            Position = position;
            Normal = normal;
        }
    }
    struct VertexPositionColor
    {
        public const uint SizeInBytes = 24; // 3 floats for position + 3 for color
        public Vector3 Position;
        public Vector3 Color;
        public VertexPositionColor(Vector3 position, Vector3 color)
        {
            Position = position;
            Color = color;
        }
    }
}