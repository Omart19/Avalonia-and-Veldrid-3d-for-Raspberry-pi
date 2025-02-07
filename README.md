# Avalonia Veldrid 3d STL Viewer
I was able to get a 3d stl model to render in Avalonia and it took me about a week and maybe a half to get this running, there was an issue with the rpi setup in the beginning but after that hurtle it was just making the view show up in the avalonia
window, theres still more i want to do and to add and most likely the even bigger product that im developing this for will help even more :) please see setup instructions before trying!

## TODO:
camera movement
Model rotation
more than 1 model per view/ or import models
Incorperate bone structure


## SET UP INSTRUCTIONS:
when running this sample you might (mostlikely) run into this error.
![errorforspriv](https://github.com/user-attachments/assets/c84f9b84-4164-4557-8840-19621d2894da)
in order to solve this issue which even happens with veldrid in general, you have to build veldrid-spirv from its source git.
make sure to git clone --recurse-submodules
git clone --recurse-submodules https://github.com/veldrid/veldrid-spirv.git 
cd into the folder
cd veldrid-spirv
run the sync-shaderc.sh to get the rest of the files.
./ext/sync-shaderc.sh
then the build file (no need to change any script eventhough stated in the refrenced instructions)
./build-native.sh -release linux-x64
after that just copy and paste the .so file to the dotnet shared folder for the dotnet version used in the project 
sudo cp veldrid-spirv/build/Release/linux-x64/libveldrid-spirv.so /opt/dotnet/shared/Microsoft.NETCore.App/9.0.0/
then the sample should run!
(its thanks to this osu fourm :') https://osu.ppy.sh/community/forums/topics/1870817?n=1 )



Working View
![avalonia3d](https://github.com/user-attachments/assets/84c01ab4-0c45-4101-aa81-b46612cd6689)
