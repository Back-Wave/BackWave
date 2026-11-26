#!/usr/bin/env bash
# Usage: tests/formal/lease-fencing/oracle-skip-locked-probe.sh <image> [--keep]
#   <image>  a gvenzl Oracle image, for example gvenzl/oracle-xe:21-slim or
#            gvenzl/oracle-free:23-slim
#   --keep   leave the container running; the next run for <image> reuses it
#
# The probe starts a private container for <image>, or reuses the one that an
# earlier --keep run left. It removes a container it started, unless --keep.
# It has no published ports and never touches the compose Oracle container.
#
# Exit codes:
#   0  Oracle never locked a row that changed after the snapshot where the
#      model skips it. A NOTE line marks a case where Oracle did less than the
#      model allows (it skipped a row the model locks, or it restarted the
#      statement).
#   1  Oracle locked a changed row that the model skips.
#   2  No valid measurement: setup failed, the control row was not locked,
#      the change missed the window, or the subquery did not sleep.
set -euo pipefail

if [[ $# -lt 1 || $# -gt 2 || ( $# -eq 2 && "$2" != "--keep" ) ]]; then
  echo "usage: $0 <image> [--keep]" >&2
  exit 2
fi
image="$1"
keep="${2:-}"
name="lease-fencing-probe-$(printf '%s' "$image" | tr -c 'a-zA-Z0-9' '-')"
user=probe
password=probe_pw_1
started=""

cleanup() {
  if [[ -n "$started" && -z "$keep" ]]; then
    docker rm -f "$name" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

if [[ "$(docker inspect -f '{{.State.Running}}' "$name" 2>/dev/null || true)" == "true" ]]; then
  echo "reusing container $name"
else
  docker rm -f "$name" >/dev/null 2>&1 || true
  echo "starting container $name from $image"
  docker run -d --name "$name" --label lease-fencing-probe=1 \
    -e ORACLE_PASSWORD=probe_sys_1 -e APP_USER="$user" -e APP_USER_PASSWORD="$password" \
    "$image" >/dev/null
  started=1
fi

for _ in $(seq 1 300); do
  if docker logs "$name" 2>&1 | grep -q 'DATABASE IS READY TO USE!'; then ready=1; break; fi
  sleep 2
done
if [[ -z "${ready:-}" ]]; then
  echo "the database in $name did not become ready" >&2
  exit 2
fi
service="$(docker exec "$name" printenv ORACLE_SID)PDB1"

sql() { docker exec -i "$name" sqlplus -s -L "$user/$password@$service"; }

sql >/dev/null <<'SQL' || { echo "setup failed" >&2; exit 2; }
WHENEVER SQLERROR EXIT 1
BEGIN EXECUTE IMMEDIATE 'DROP TABLE probe_jobs'; EXCEPTION WHEN OTHERS THEN NULL; END;
/
CREATE TABLE probe_jobs (job_id NUMBER PRIMARY KEY, state NUMBER, attempt NUMBER, lease_expiry TIMESTAMP WITH TIME ZONE, note VARCHAR2(20));
CREATE OR REPLACE FUNCTION probe_slow RETURN NUMBER IS BEGIN DBMS_SESSION.SLEEP(4); RETURN 1; END;
/
CREATE OR REPLACE FUNCTION probe_epoch RETURN NUMBER IS
  d INTERVAL DAY(9) TO SECOND(6) := SYSTIMESTAMP - TIMESTAMP '1970-01-01 00:00:00 UTC';
BEGIN
  RETURN EXTRACT(DAY FROM d) * 86400 + EXTRACT(HOUR FROM d) * 3600 + EXTRACT(MINUTE FROM d) * 60 + EXTRACT(SECOND FROM d);
END;
/
EXIT
SQL
echo "oracle: $(printf '%s\n' "SET HEADING OFF FEEDBACK OFF" \
  "SELECT product || ' ' || version_full FROM product_component_version WHERE ROWNUM = 1;" | sql | grep -m1 .)"

# The ExpireLeases statement (OracleJobStore.cs:1473-1484), with the outer WHERE
# and ORDER BY that each case passes. Only outerwhere passes an outer WHERE.
sweep_sql() {
  local outer_where="$1" outer_order="$2"
  cat <<SQL
SET SERVEROUTPUT ON FEEDBACK OFF
WHENEVER SQLERROR EXIT 1
DECLARE
  t0 NUMBER := probe_epoch;
  n PLS_INTEGER := 0;
BEGIN
  FOR r IN (
    SELECT job_id, attempt FROM probe_jobs
    WHERE $outer_where job_id IN (
      SELECT job_id FROM (
        SELECT job_id FROM probe_jobs
        WHERE state = 2 AND lease_expiry <= SYSTIMESTAMP AND probe_slow() = 1
        ORDER BY lease_expiry
      ) WHERE ROWNUM <= 10
    )
    $outer_order
    FOR UPDATE SKIP LOCKED
  ) LOOP
    n := n + 1;
  END LOOP;
  DBMS_OUTPUT.PUT_LINE('sweep ' || TO_CHAR(t0, 'FM9999999999.000') || ' '
    || TO_CHAR(probe_epoch, 'FM9999999999.000') || ' ' || n);
  ROLLBACK;
END;
/
EXIT
SQL
}

change_sql() {
  local change="$1"
  cat <<SQL
SET SERVEROUTPUT ON FEEDBACK OFF
WHENEVER SQLERROR EXIT 1
DECLARE
  t1 NUMBER;
BEGIN
  DBMS_SESSION.SLEEP(1.5);
  t1 := probe_epoch;
  $change
  COMMIT;
  DBMS_OUTPUT.PUT_LINE('change ' || TO_CHAR(t1, 'FM9999999999.000') || ' '
    || TO_CHAR(probe_epoch, 'FM9999999999.000'));
END;
/
EXIT
SQL
}

failures=0
invalid=0
notes=0
work="$(mktemp -d "${TMPDIR:-/tmp}/oracle-skip-locked-probe.XXXXXX")"

change_in_window() {
  local snapshot="$1" change_start="$2" change_end="$3"
  (( $(echo "$change_start > $snapshot && $change_end < $snapshot + 3.5" | bc) ))
}

run_case() {
  local case="$1" pred="$2" order="$3" change="$4" predict="$5" attempt want
  want=$([[ "$predict" == lock ]] && echo 1 || echo 0)
  for attempt in 1 2 3; do
    sql >/dev/null <<'SQL' || { echo "$case: reset failed" >&2; exit 2; }
WHENEVER SQLERROR EXIT 1
DELETE FROM probe_jobs;
INSERT INTO probe_jobs VALUES (1, 2, 1, SYSTIMESTAMP - INTERVAL '1' MINUTE, NULL);
COMMIT;
EXIT
SQL
    sweep_sql "$pred" "$order" | sql >"$work/sweep" &
    local pid=$!
    change_sql "$change" | sql >"$work/change"
    wait "$pid" || true
    if ! grep -q '^sweep ' "$work/sweep" || ! grep -q '^change ' "$work/change"; then
      echo "$case: INVALID (a session failed)"; cat "$work/sweep" "$work/change"
      invalid=$((invalid + 1)); return
    fi
    read -r _ t0 t9 locked < <(grep '^sweep ' "$work/sweep")
    read -r _ c0 c9 < <(grep '^change ' "$work/change")
    local elapsed at verdict
    elapsed="$(echo "$t9 - $t0" | bc)"
    at="$(echo "$c0 - $t0" | bc)"
    if [[ "$change" == "NULL;" ]] || change_in_window "$t0" "$c0" "$c9"; then
      if (( $(echo "$elapsed < 3.5" | bc) )); then
        verdict="INVALID (the subquery did not sleep)"; invalid=$((invalid + 1))
      elif (( $(echo "$elapsed > 6" | bc) )); then
        # A restart takes a new snapshot after the change, so the row it
        # locks did not change after that snapshot.
        verdict="NOTE (the statement restarted; the model has no restart)"; notes=$((notes + 1))
      elif [[ "$locked" == "$want" ]]; then
        verdict=ok
      elif [[ "$change" == "NULL;" ]]; then
        verdict="INVALID (the control row was not locked)"; invalid=$((invalid + 1))
      elif [[ "$predict" == skip ]]; then
        verdict="FAIL (Oracle locked a row that changed after the snapshot; the model skips it)"
        failures=$((failures + 1))
      else
        verdict="NOTE (Oracle skipped a row that the model locks)"; notes=$((notes + 1))
      fi
      printf '%-10s locked=%s model=%-4s elapsed=%5.2fs change_at=+%.2fs  %s\n' \
        "$case" "$locked" "$predict" "$elapsed" "$at" "$verdict"
      return
    fi
    echo "$case: the change committed at +${at}s, outside the window; retrying ($attempt of 3)"
  done
  echo "$case: INVALID (the change missed the window three times)"
  invalid=$((invalid + 1))
}

order="ORDER BY lease_expiry"
outcome="UPDATE probe_jobs SET state = 3, lease_expiry = NULL WHERE job_id = 1;"
run_case none       "" "$order" "NULL;" lock
run_case outcome    "" "$order" "$outcome" skip
run_case heartbeat  "" "$order" "UPDATE probe_jobs SET lease_expiry = lease_expiry - INTERVAL '1' SECOND WHERE job_id = 1;" skip
run_case selected   "" "$order" "UPDATE probe_jobs SET attempt = 2 WHERE job_id = 1;" skip
run_case outerwhere "state = 2 AND" "$order" "UPDATE probe_jobs SET state = 4 WHERE job_id = 1;" skip
run_case inner      "" "$order" "UPDATE probe_jobs SET state = 4 WHERE job_id = 1;" lock
run_case unread     "" "$order" "UPDATE probe_jobs SET note = 'touched' WHERE job_id = 1;" lock
run_case noorder    "" ""       "$outcome" lock
rm -rf "$work"

if [[ $failures -gt 0 ]]; then
  echo "Oracle locked a row that changed after the snapshot, where the model skips it, on $image." >&2
  echo "Fix LockLevelCols in LeaseFencing.tla and rerun check.sh --all before you trust an Oracle pass." >&2
  exit 1
fi
if [[ $invalid -gt 0 ]]; then
  echo "$invalid case(s) did not give a valid measurement." >&2
  exit 2
fi
if [[ $notes -gt 0 ]]; then
  echo "Oracle did less than the model allows in $notes case(s) on $image. The Oracle configs still hold."
  echo "expire-no-outer-where-and-order-oracle rests on case noorder: if noorder is a NOTE, that mutant does not match Oracle."
  exit 0
fi
echo "Every case matches the model's Oracle SKIP LOCKED rule on $image."
