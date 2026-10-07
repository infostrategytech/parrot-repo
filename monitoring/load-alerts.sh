#!/usr/bin/env bash
# Loads the message processing alert rules into a Grafana you can only reach over
# HTTP (no access to its provisioning folder).
#
#   GRAFANA_URL=https://grafana.example.com GRAFANA_TOKEN=glsa_... \
#   PROMETHEUS_DATASOURCE_UID=<uid> monitoring/load-alerts.sh
#
# GRAFANA_TOKEN: a service account token with the Editor role (Administration ->
# Service accounts). PROMETHEUS_DATASOURCE_UID: the uid of the Prometheus data
# source that scrapes Parrot (Connections -> Data sources -> it; the uid is in the URL).
# Creates the "Parrot" folder if needed and replaces the rule group, so it is safe to re-run.
# Rules created this way are marked as provisioned: change them here, not in the UI.
set -euo pipefail
: "${GRAFANA_URL:?}" "${GRAFANA_TOKEN:?}" "${PROMETHEUS_DATASOURCE_UID:?}"
HERE="$(cd "$(dirname "$0")" && pwd)"
PAYLOAD="$(mktemp)"
trap 'rm -f "$PAYLOAD"' EXIT

# Fails on any HTTP error, printing Grafana's reason.
api() {
  local body status
  body="$(curl -sS -w '\n%{http_code}' -H "Authorization: Bearer $GRAFANA_TOKEN" -H 'Content-Type: application/json' "$@")"
  status="${body##*$'\n'}"; body="${body%$'\n'*}"
  if [[ "$status" -ge 400 ]]; then echo "Grafana answered $status: $body" >&2; exit 1; fi
  printf '%s' "$body"
}

FOLDERS="$(api "$GRAFANA_URL/api/folders")"
FOLDER_UID=$(python3 -c 'import json,sys; print(next((f["uid"] for f in json.loads(sys.argv[1]) if f["title"] == "Parrot"), ""))' "$FOLDERS")
if [[ -z "$FOLDER_UID" ]]; then
  CREATED="$(api -X POST "$GRAFANA_URL/api/folders" -d '{"title":"Parrot"}')"
  FOLDER_UID=$(python3 -c 'import json,sys; print(json.loads(sys.argv[1])["uid"])' "$CREATED")
fi

python3 - "$HERE/message-processor-alerts.json" "$PROMETHEUS_DATASOURCE_UID" "$FOLDER_UID" > "$PAYLOAD" <<'PY'
import json, sys
path, ds, folder = sys.argv[1:4]
group = json.load(open(path))["groups"][0]
for rule in group["rules"]:
    for d in rule["data"]:
        if d["datasourceUid"] == "PROMETHEUS_DATASOURCE_UID":
            d["datasourceUid"] = ds
    rule["folderUID"], rule["ruleGroup"], rule["orgID"] = folder, group["name"], group["orgId"]
print(json.dumps({"title": group["name"], "interval": 60, "rules": group["rules"]}))
PY

api -X PUT "$GRAFANA_URL/api/v1/provisioning/folder/$FOLDER_UID/rule-groups/parrot-message-processing" \
  -d @"$PAYLOAD" > /dev/null
echo "Loaded $(python3 -c 'import json; print(len(json.load(open("'"$HERE"'/message-processor-alerts.json"))["groups"][0]["rules"]))') alert rules into folder Parrot ($FOLDER_UID)."
