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
using Avalonia.Interactivity; // For RoutedEventArgs

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
    vec3 normal = normalize(v_Normal);
    fsout_Color = vec4(abs(normal), 1.0); // Use absolute values
}";

        public VeldridControl()
        {
            this.AttachedToVisualTree += OnAttachedToVisualTree;
            this.DetachedFromVisualTree += OnDetachedFromVisualTree;
            this.Loaded += OnLoaded; // Add Loaded event handler
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
            _avaloniaBitmap?.Dispose(); _avaloniaBitmap = null;

            // Recreate size-dependent resources.
            CreateOffscreenFramebuffer();
            CreateStagingTexture();
            CreateAvaloniaBitmap();

            // No need to call InvalidateVisual here, its done in render
        }


        private void CreateResources()
        {
            if (_graphicsDevice == null) return;

            ResourceFactory factory = _graphicsDevice.ResourceFactory;

            // --- Load STL ---
            var (stlVertices, stlIndices) = LoadSTL("model.stl"); // Load the STL *data*
            Console.WriteLine($"Loaded {stlVertices.Length} vertices and {stlIndices.Length} indices.");

            if (stlVertices.Length == 0)
            {
                Console.WriteLine("ERROR: STL loading failed.");
                return;
            }
            _modelMatrix = ComputeModelMatrix(stlVertices);

            _vertexBuffer = factory.CreateBuffer(new BufferDescription((uint)(stlVertices.Length * VertexPositionNormal.SizeInBytes), BufferUsage.VertexBuffer));
            _graphicsDevice.UpdateBuffer(_vertexBuffer, 0, stlVertices);  // Use stlVertices

            _indexBuffer = factory.CreateBuffer(new BufferDescription((uint)(stlIndices.Length * sizeof(ushort)), BufferUsage.IndexBuffer));
            _graphicsDevice.UpdateBuffer(_indexBuffer, 0, stlIndices); // Use stlIndices

            _mvpBuffer = factory.CreateBuffer(new BufferDescription(3 * 64, BufferUsage.UniformBuffer));
            _mvpLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("MVP", ResourceKind.UniformBuffer, ShaderStages.Vertex)));
            _mvpResourceSet = factory.CreateResourceSet(new ResourceSetDescription(_mvpLayout, _mvpBuffer));
            // --- End Load STL ---



            VertexLayoutDescription vertexLayout = new VertexLayoutDescription(
            new VertexElementDescription("Position", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
            new VertexElementDescription("Normal", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3) // Add back the normal
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
                    cullMode: FaceCullMode.Back, // Use back-face culling
                    fillMode: PolygonFillMode.Solid,
                    frontFace: FrontFace.CounterClockwise, // Important:  Counter-Clockwise winding order
                    depthClipEnabled: true,
                    scissorTestEnabled: false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new ResourceLayout[] { _mvpLayout }, // Use the MVP layout
                ShaderSet = new ShaderSetDescription(new VertexLayoutDescription[] { vertexLayout }, _shaders),
                Outputs = _offscreenFramebuffer.OutputDescription
            };
            _pipeline = factory.CreateGraphicsPipeline(pipelineDescription);
            _commandList = factory.CreateCommandList();

            CreateAvaloniaBitmap();
            _resourcesCreated = true;

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
        private void UpdateMVP()
        {
            // Systematically try different camera positions:
            // 1. Original position:
            //Vector3 cameraPosition = new Vector3(0, 0, 5);

            // 2. Further back:
            //Vector3 cameraPosition = new Vector3(0, 0, 10);

            // 3. From the side:
            // Vector3 cameraPosition = new Vector3(5, 0, 0);

            // 4. From above:
            // Vector3 cameraPosition = new Vector3(0, 5, 0);

            // 5. From below and to the side:
            Vector3 cameraPosition = new Vector3(2, -2, 2);

            Matrix4x4 viewMatrix = Matrix4x4.CreateLookAt(cameraPosition, new Vector3(0, 0, 0), Vector3.UnitY);
            float aspect = (float)Math.Max(1, Bounds.Width) / (float)Math.Max(1, Bounds.Height);
            Matrix4x4 projectionMatrix = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4, aspect, 0.1f, 100f);
            Matrix4x4 modelMatrix = _modelMatrix; // Use the model matrix
            Matrix4x4[] mvpMatrices = new Matrix4x4[] { modelMatrix, viewMatrix, projectionMatrix };
            _graphicsDevice.UpdateBuffer(_mvpBuffer, 0, mvpMatrices);

            Console.WriteLine($"Camera Position: {cameraPosition}"); // Print camera position
        }
        private Matrix4x4 ComputeModelMatrix(VertexPositionNormal[] vertices)
        {
            if (vertices.Length == 0)
            {
                return Matrix4x4.Identity; // CRUCIAL: Return identity if no vertices
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
            Matrix4x4 translation = Matrix4x4.CreateTranslation(-center);
            Matrix4x4 scale = Matrix4x4.CreateScale(scaleFactor);
            Console.WriteLine($"Scale Factor: {scaleFactor}");
            return translation * scale; // Corrected matrix multiplication
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

                        // *** CHECK FOR ZERO-LENGTH NORMALS ***
                        if (normal.LengthSquared() < 0.0001f) // Use LengthSquared for efficiency
                        {
                            Console.WriteLine($"Warning: Triangle {i} has a near-zero normal: {normal}");
                            // Option 1: Skip this triangle (add 'continue;')
                            // Option 2: Assign a default normal (e.g., Vector3.UnitY)
                            normal = Vector3.UnitY; // Example: Assign a default normal
                        }


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
            UpdateMVP();
            _commandList.Begin();
            _commandList.SetFramebuffer(_offscreenFramebuffer);
            _commandList.ClearColorTarget(0, RgbaFloat.Black); // Clear to Black
            _commandList.ClearDepthStencil(1f);

            // Viewport and Scissor
            Console.WriteLine($"Draw - Bounds: {Bounds}");
            _commandList.SetViewport(0, new Viewport(0, 0, (float)Bounds.Width, (float)Bounds.Height, 0, 1));
            _commandList.SetScissorRect(0, 0, 0, (uint)Bounds.Width, (uint)Bounds.Height);

            _commandList.SetPipeline(_pipeline);
            _commandList.SetGraphicsResourceSet(0, _mvpResourceSet); // Use resource set
            _commandList.SetVertexBuffer(0, _vertexBuffer);
            _commandList.SetIndexBuffer(_indexBuffer, IndexFormat.UInt16);
            _commandList.DrawIndexed(
                indexCount: (uint)(_indexBuffer.SizeInBytes / sizeof(ushort)),
                instanceCount: 1,
                indexStart: 0,
                vertexOffset: 0,
                instanceStart: 0); // Draw the triangle

            _commandList.CopyTexture(_offscreenColorTexture, _stagingTexture);
            _commandList.End();
            _graphicsDevice.SubmitCommands(_commandList);
            // _graphicsDevice.WaitForIdle(); // REMOVED
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
}