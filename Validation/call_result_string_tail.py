"""Typed behavior oracle for a field getter and guarded string tail call."""

from behavior_oracle import verify_report


NULL_FAILURE = "System.NullReferenceException"


def _row(kind, scenario, failure, result, texts, same_node, shared,
         value, other_texts=None, host_null=False):
    return {
        "kind": kind, "scenario": scenario, "failure": failure, "result": result,
        "textCalls": texts, "otherTextCalls": other_texts,
        "nodeSameWitness": same_node, "hostsShareNode": shared,
        "value": value, "hostNull": host_null,
    }


def observations():
    return [
        {"kind": "constructor", "nodeNull": True, "valueNull": True,
         "textCalls": 0},
        _row("single", "value", "none", "alpha", 5, True, None,
             "alpha", other_texts=7),
        _row("single", "missing-node", NULL_FAILURE, None, 4, False,
             None, "alpha", other_texts=7),
        _row("single", "null-string", "none", None, 5, True,
             None, None, other_texts=7),
        _row("single", "host-null", NULL_FAILURE, None, 4, None,
             None, "alpha", other_texts=7, host_null=True),
        _row("single", "other-node", "none", "beta", 4, False,
             None, "alpha", other_texts=8),
        _row("sequence", "first-host", "none", "first", 8, True,
             True, "first"),
        _row("sequence", "second-host", "none", "first", 9, True,
             True, "first"),
        _row("sequence", "changed-value", "none", "second", 10,
             True, True, "second"),
        _row("sequence", "missing-node", NULL_FAILURE, None, 10,
             False, False, "second"),
        _row("sequence", "restored", "none", "second", 11,
             True, True, "second"),
        _row("sequence", "null-string", "none", None, 12,
             True, True, None),
    ]


def verify(path, stage, version):
    return verify_report(
        path, stage, version, "call-result-string-tail", observations(), 5,
        "Five concrete methods; instance getter before a guarded string-return "
        "tail, null owner/node/string, shared receiver identity, repeated calls, "
        "and unchanged witness state on exceptions")
