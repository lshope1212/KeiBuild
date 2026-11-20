using KeiBuild.Research.Data;

namespace KeiBuild.Research.Discovery;

internal sealed record VehicleSystem(string Key, string English, string Japanese, params string[] Terms)
{
    internal static VehicleSystem[] All { get; } =
    [
        new("engine", "engine", "エンジン", "cylinder", "displacement", "E07A", "排気量"),
        new("cooling", "cooling", "冷却", "radiator", "coolant", "water pump", "ラジエータ"),
        new("fuel", "fuel system", "燃料", "carburetor", "fuel pump", "燃料ポンプ"),
        new("ignition", "ignition", "点火", "spark plug", "distributor", "プラグ"),
        new("transmission", "transmission", "変速機", "gearbox", "clutch", "ミッション"),
        new("drivetrain", "drivetrain", "駆動", "differential", "axle", "4WD", "四輪駆動"),
        new("suspension", "suspension", "サスペンション", "spring", "shock absorber", "足回り"),
        new("brakes", "brakes", "ブレーキ", "brake", "caliper"),
        new("electrical", "electrical", "電装", "wiring", "alternator", "battery", "配線"),
        new("steering", "steering", "ステアリング", "tie rod", "操舵"),
        new("body", "body", "車体", "cab panel", "interior", "内装", "wheelbase", "寸法")
    ];

    internal bool Covers(string subject, string attribute) =>
        Terms.Append(English).Append(Japanese).Any(term => (subject + " " + attribute).Contains(term, StringComparison.OrdinalIgnoreCase));
}

