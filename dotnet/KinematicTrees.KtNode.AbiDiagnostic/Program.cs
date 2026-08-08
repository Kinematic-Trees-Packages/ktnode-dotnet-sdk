using KinematicTrees.KtNode;

var abi = Runtime.AbiVersion;
var version = Runtime.RuntimeVersion;
Console.WriteLine($"kt_node ABI {abi.Major}.{abi.Minor}");
Console.WriteLine($"kt_node runtime {version.Major}.{version.Minor}.{version.Patch}");
Console.WriteLine($"build_id {Runtime.BuildId}");
