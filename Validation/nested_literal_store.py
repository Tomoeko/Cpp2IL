"""Independent capture, null-failure, aliasing and fixed-width store oracle."""

import json


def signed32(value):
    return (value + 2147483648) % 4294967296 - 2147483648


def expected_observations():
    observations = []
    for operation in range(5):
        for seed in (-2147483648, 0, 2147483647):
            for enabled in (False, True):
                for configuration in range(6):
                    for prior in (False, True):
                        for increment in (-7, 2147483647):
                            first = {"enabled": enabled, "signed": seed, "unsigned": seed % 4294967296, "neighbor": 37}
                            second_seed = signed32(seed ^ 37)
                            second = {"enabled": not enabled, "signed": second_seed, "unsigned": second_seed % 4294967296, "neighbor": 53}
                            replacement = {"enabled": not enabled, "signed": -17, "unsigned": 29, "neighbor": 71}
                            first_binding = "null" if configuration in (1, 3) else "first"
                            second_binding = "null" if configuration in (2, 3) else "first" if configuration == 4 else "second"
                            marker, prior_after = seed, not prior
                            exception = "none"
                            if configuration == 5:
                                exception = "NullReferenceException"
                            else:
                                # Capture First before any intervening operation changes the owner.
                                captured = None if first_binding == "null" else first
                                if operation == 0:
                                    prior_after = prior
                                elif operation == 1:
                                    marker = signed32(marker + increment)
                                elif operation == 3:
                                    marker = signed32(marker + 1)
                                    first_binding = "replacement"
                                if captured is None:
                                    exception = "NullReferenceException"
                                else:
                                    if operation in (0, 3):
                                        captured["enabled"] = True
                                    elif operation == 1:
                                        captured["signed"] = -1
                                    elif operation == 2:
                                        captured["unsigned"] = 2147483648
                                    else:
                                        captured.update(enabled=False, signed=2147483647, unsigned=4294967295)
                                    if operation != 4:
                                        next_target = None if second_binding == "null" else first if second_binding == "first" else second
                                        if next_target is None:
                                            exception = "NullReferenceException"
                                        else:
                                            if operation in (0, 3):
                                                next_target["enabled"] = False
                                            elif operation == 1:
                                                next_target["signed"] = -2147483648
                                            else:
                                                next_target["unsigned"] = 4294967295
                                            marker = signed32(marker + operation + 1)
                            observations.append({"operation": operation, "seed": seed, "enabled": enabled,
                                                 "configuration": configuration, "prior": prior, "increment": increment,
                                                 "marker": marker, "priorAfter": prior_after,
                                                 "firstAfter": first, "secondAfter": second, "replacementAfter": replacement,
                                                 "exception": exception, "firstBinding": first_binding, "secondBinding": second_binding})
    return observations


def verify(path, stage, version):
    report = json.loads(path.read_text(encoding="utf-8"))
    platform = {"editor": "WindowsEditor", "player": "WindowsPlayer"}.get(stage)
    if (platform is None or report.get("unityVersion") != version or report.get("stage") != stage or
            report.get("platform") != platform or report.get("profile") != "nested-literal-store"):
        raise ValueError("Nested store report has the wrong target or stage")
    expected = expected_observations()
    if report.get("observations") != expected:
        raise ValueError("Nested store observations differ from the independent oracle")
    return {"status": "passed", "observations": len(expected), "methods": 9,
            "platform": platform, "profile": "nested-literal-store"}
