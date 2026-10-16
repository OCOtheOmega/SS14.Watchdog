{ flake, lib, buildFHSEnv, buildDotnetModule, dotnetCorePackages, git, python3, zstd, zlib, tzdata }:
let
  rev = flake.rev or "dirty";
  dotnet-sdk = with dotnetCorePackages;
    combinePackages [ sdk_10_0 ];
  watchdog = buildDotnetModule {
    inherit dotnet-sdk;
    name = "space-station-14-watchdog-${rev}";

    src = lib.cleanSourceWith {
      src = ../.;
      filter = path: type: !(builtins.elem (baseNameOf path) [ "bin" "obj" ".git" "dist" ]);
    };

    projectFile = [ "SS14.Watchdog/SS14.Watchdog.csproj" ];

    buildType = "Release";
    selfContainedBuild = false;

    # Generated using "fetch-deps" flake app output.
    nugetDeps = ./deps.json;

    runtimeDeps = [ git python3 zstd zlib tzdata ];

    dotnet-runtime = with dotnetCorePackages;
      combinePackages [ runtime_10_0 aspnetcore_10_0 ];

    executables = [ "SS14.Watchdog" ];
  };
in
buildFHSEnv {
  name = "SS14.Watchdog";

  targetPkgs = pkgs: [ watchdog git python3 dotnet-sdk zstd tzdata ];

  runScript = "SS14.Watchdog";

  passthru = watchdog.passthru // {
    name = "space-station-14-watchdog-wrapped-${rev}";
  };
}
