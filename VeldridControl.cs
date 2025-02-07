// using Avalonia;
// using Avalonia.Controls;
// using Avalonia.Markup.Xaml;
// using Avalonia.Media;
// using Avalonia.Platform;
// using Avalonia.VisualTree;
// using System;
// using Veldrid;
// using Veldrid.Sdl2;
// using Veldrid.StartupUtilities;

// namespace VeldridSTLViewer;


// public class VeldridControl : Control
// {
//     private GraphicsDevice _graphicsDevice;
//     private Swapchain _swapchain;
//     private CommandList _commandList;

//     public VeldridControl()
//     {
//         this.Initialized += OnInitialized;
//         // this.AttachedToVisualTree += OnAttachedToVisualTree;
//         this.DetachedFromVisualTree += OnDetachedFromVisualTree;
//     }
// //     private void OnAttachedToVisualTree(object sender, VisualTreeAttachmentEventArgs e)
// // {
// //     CompositionTarget.Rendering += OnRendering;
// // }


//     private void OnInitialized(object sender, EventArgs e)
//     {
//         // Initialize Veldrid components here
//         CreateGraphicsDevice();
//     }

//     private void CreateGraphicsDevice()
//     {
//         GraphicsDeviceOptions options = new GraphicsDeviceOptions
//         {
//             PreferDepthRangeZeroToOne = true,
//             PreferStandardClipSpaceYDirection = true
//         };

//         Sdl2Window dummyWindow = new Sdl2Window("", 0, 0, 100, 100, SDL_WindowFlags.Hidden, false);
//         _graphicsDevice = VeldridStartup.CreateGraphicsDevice(dummyWindow, options, GraphicsBackend.Vulkan);

//         _commandList = _graphicsDevice.ResourceFactory.CreateCommandList();
//         AttachResize();
//         ReCreateResources();
//     }

//     private void ReCreateResources()
//     {
//         _swapchain?.Dispose();
//         var platformHandle = this; // Platform-specific handle

//         if (platformHandle is IPlatformHandle platform)
//      {

//         SwapchainDescription swapchainDescription = new SwapchainDescription(
//             SwapchainSource.CreateXlib( platform.Handle,platform.Handle),
//             (uint)this.Bounds.Width, (uint)this.Bounds.Height, 
//             Veldrid.PixelFormat.R32_G32_B32_A32_UInt, true);
//         _swapchain = _graphicsDevice.ResourceFactory.CreateSwapchain(swapchainDescription);
//     }
//     }

//     protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
//     {
//         base.OnAttachedToVisualTree(e);
//         this.GetObservable(BoundsProperty).Subscribe(_ => ReCreateResources());
//     }

//     private void AttachResize()
//     {
//         // Handle resize events to recreate resources if needed
//         this.SizeChanged += (s, e) => ReCreateResources();
//     }

//     private void OnDetachedFromVisualTree(object sender, VisualTreeAttachmentEventArgs e)
//     {
//         _graphicsDevice?.Dispose();
//         _swapchain?.Dispose();
//         _commandList?.Dispose();
//     }

//     public void Render()
//     {
//         if (_graphicsDevice == null) return;

//         _commandList.Begin();
//         _commandList.SetFramebuffer(_swapchain.Framebuffer);
//         _commandList.ClearColorTarget(0, RgbaFloat.CornflowerBlue);  // Clear to a noticeable color

//         // Perform rendering commands here
//         _commandList.End();
//         _graphicsDevice.SubmitCommands(_commandList);
//         _graphicsDevice.SwapBuffers(_swapchain);
//     }
// }


// // public class VeldridControl : Control
// // {
// //     private GraphicsDevice _graphicsDevice;
// //     private Swapchain _swapchain;

// //     public VeldridControl()
// //     {
// //         this.Initialized += OnInitialized;
// //     }

// //     private void OnInitialized(object sender, EventArgs e)
// //     {
// //         CreateGraphicsDevice();
// //     }

// // private void CreateGraphicsDevice()
// // {
// //     GraphicsDeviceOptions options = new GraphicsDeviceOptions(
// //         debug: false,
// //         swapchainDepthFormat: Veldrid.PixelFormat.R32_Float,
// //         syncToVerticalBlank: true,
// //         resourceBindingModel: ResourceBindingModel.Improved);

// //     var window = this.VisualRoot as Window;
// //     if (window == null)
// //         throw new InvalidOperationException("VeldridControl must be hosted in a Window");

// //     var platformHandle = this; // Platform-specific handle
// //     SwapchainSource swapchainSource;

// //     if (platformHandle is IPlatformHandle platform)
// //     {
        
            
// //         swapchainSource = SwapchainSource.CreateWin32(platform.Handle, platform.Handle);
               
        
// //     }
// //     else
// //     {
// //         throw new InvalidOperationException("Unable to obtain platform handle.");
// //     }

// //     _graphicsDevice = GraphicsDevice.CreateVulkan(options);

// //     SwapchainDescription swapchainDescription = new SwapchainDescription(
// //         swapchainSource,
// //         (uint)window.Bounds.Width,
// //         (uint)window.Bounds.Height,
// //         null,
// //         true,
// //         true);

// //     _swapchain = _graphicsDevice.ResourceFactory.CreateSwapchain(swapchainDescription);
// // }

// //     private SwapchainSource GetSwapchainSource(IPlatformHandle handle)
// //     {
// //         return SwapchainSource.CreateXlib(handle.Handle, (nint)handle.Handle);
// //     }

// //     public override void Render(DrawingContext context)
// //     {
// //         base.Render(context);

// //         if (_graphicsDevice != null && _swapchain != null)
// //         {
// //             _graphicsDevice.SwapBuffers(_swapchain);
// //             _graphicsDevice.WaitForIdle();
// //         }
// //     }

// //     protected override Size MeasureOverride(Size availableSize)
// //     {
// //         return new Size(800, 600); // Adjust size as needed
// //     }
// // }
