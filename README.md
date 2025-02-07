# Avalonia Veldrid 3D STL Viewer

I successfully integrated a 3D STL model renderer into an Avalonia application. The process took approximately a week and a half, including overcoming an initial issue with the Raspberry Pi setup.  After resolving the Raspberry Pi configuration, the main challenge was displaying the 3D view within the Avalonia window.  This project is still under development, and I have plans for further enhancements, likely driven by a larger product this component is being built for.  **Please review the setup instructions carefully before attempting to run the sample.**

## TODO:

*   Camera movement
*   Model rotation
*   Support for multiple models per view or importing models
*   Incorporate bone structure

## Setup Instructions:

When running this sample, you will likely encounter the following error related to Veldrid's SPIR-V support:

![errorforspriv](https://github.com/user-attachments/assets/c84f9b84-4164-4557-8840-19621d2894da)

This error is common with Veldrid, even outside of this specific project. To resolve it, you must build `veldrid-spirv` from its source code.

1.  **Clone the `veldrid-spirv` repository:**  Make sure to use the `--recurse-submodules` flag to fetch all necessary dependencies:

    ```bash
    git clone --recurse-submodules [https://github.com/veldrid/veldrid-spirv.git](https://github.com/veldrid/veldrid-spirv.git)
    ```

2.  **Navigate to the cloned directory:**

    ```bash
    cd veldrid-spirv
    ```

3.  **Synchronize shaderc:**  Run the provided script to obtain the required shaderc files:

    ```bash
    ./ext/sync-shaderc.sh
    ```

4.  **Build the native library:** Build the library for your platform (Linux x64 in this case).  You do *not* need to modify the build script, despite what might be mentioned in other instructions.

    ```bash
    ./build-native.sh -release linux-x64
    ```

5.  **Copy the library file:**  Copy the resulting `.so` file to the appropriate .NET shared framework directory.  Replace `9.0.0` with the *exact* .NET version your project is using.  You can find this version by looking at your project's `.csproj` file (look for `<TargetFramework>net9.0</TargetFramework>` or similar, use the version number there, eg: 6.0.25, 7.0.14, 8.0.2). Use the correct path for the shared folder of the correct dotnet version!
    ```bash
    sudo cp veldrid-spirv/build/Release/linux-x64/libveldrid-spirv.so /opt/dotnet/shared/Microsoft.NETCore.App/9.0.0/
    ```

    *   **Important:** If the `/opt/dotnet/shared/Microsoft.NETCore.App/9.0.0/` directory doesn't exist, you either have the wrong .NET version, or .NET isn't installed in the standard location.  You might have .NET installed through a package manager in a different place (e.g., `/usr/share/dotnet/shared/Microsoft.NETCore.App/`).  Use `dotnet --info` to get information about your .NET installation, which can help you locate the correct shared framework directory.
    *  If the file already exist.  
      ```bash
      sudo mv /opt/dotnet/shared/Microsoft.NETCore.App/9.0.0/libveldrid-spirv.so /opt/dotnet/shared/Microsoft.NETCore.App/9.0.0/libveldrid-spirv.so.bak
      sudo cp veldrid-spirv/build/Release/linux-x64/libveldrid-spirv.so /opt/dotnet/shared/Microsoft.NETCore.App/9.0.0/
      ```
    *   **Note for Raspberry Pi:** If you are building directly on a Raspberry Pi, the architecture might be `arm64` or `armhf` instead of `linux-x64`.  Adjust the `build-native.sh` command accordingly (e.g., `./build-native.sh -release linux-arm64`).

After completing these steps, the sample project should run without the SPIR-V error.

(Thanks to this osu forum post for the solution: [https://osu.ppy.sh/community/forums/topics/1870817?n=1](https://osu.ppy.sh/community/forums/topics/1870817?n=1))

## Working View

![avalonia3d](https://github.com/user-attachments/assets/84c01ab4-0c45-4101-aa81-b46612cd6689)
