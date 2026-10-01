"""Stage-specific oracle for preserved framework reference declarations and identity returns."""

from behavior_oracle import verify_report


FAMILIES = (
    ("regex", "Pattern", "RegexIdentity", "System.Text.RegularExpressions.Regex", "System"),
    ("expression", "Projection", "ExpressionIdentity", "System.Linq.Expressions.Expression", "System.Core"),
    ("xml", "Document", "XmlIdentity", "System.Xml.XmlDocument", "System.Xml"),
)


def observations(stage):
    if stage not in ("editor", "player"):
        raise ValueError("Framework reference observations require an explicit editor or player stage")
    rows = [{"kind": "declarations", "type": "NativeFrameworkReferenceTransportFixture.Probe",
             "visibility": 1, "sealed": True, "constructors": 1,
             "fields": [{"name": field, "type": type_name} for _, field, _, type_name, _ in FAMILIES],
             "methods": [{"name": method, "static": True, "return": type_name,
                          "parameterCount": 1, "parameter": type_name}
                         for _, _, method, type_name, _ in FAMILIES]}]
    token = "b77a5c561934e089" if stage == "editor" else "7cec85d7bea7798e"
    rows.extend({"kind": "frameworkIdentity", "family": family, "name": assembly,
                 "version": "4.0.0.0", "culture": "", "token": token, "signatureBound": True}
                for family, _, _, _, assembly in FAMILIES)
    rows.append({"kind": "defaults", "patternNull": True, "projectionNull": True, "documentNull": True})
    for family, _, _, _, _ in FAMILIES:
        for case in ("null", "first", "second", "reused", "stored", "afterReplacement"):
            rows.append({"kind": "identity", "family": family, "case": case,
                         "resultNull": case == "null", "returnedIncoming": True,
                         "matchesFirst": case in ("first", "reused", "afterReplacement"),
                         "matchesSecond": case in ("second", "stored"), "fieldUnchanged": True})
    rows.append({"kind": "frameworkBehavior", "regexAccept": True, "regexReject": False,
                 "expressionKind": 9, "expressionValue": 17, "xmlName": "root", "xmlValue": "17"})
    return rows


def verify(path, stage, version):
    return verify_report(path, stage, version, "native-framework-reference-transport", observations(stage), 4,
                         "full four-method assembly; framework declarations, stage-specific identities, null and aliased returns")
