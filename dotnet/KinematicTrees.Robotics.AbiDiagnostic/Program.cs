using KinematicTrees.Robotics;

var abi = Runtime.AbiVersion;
var version = Runtime.RuntimeVersion;
Console.WriteLine($"kt_robotics ABI {abi.Major}.{abi.Minor}");
Console.WriteLine($"kt_robotics runtime {version.Major}.{version.Minor}.{version.Patch}");
Console.WriteLine($"build_id {Runtime.BuildId}");
