#!/opt/homebrew/bin/python3
"""Restart the running MusicHoarder API after sustained failed health checks."""
import json
import subprocess
from datetime import datetime, timezone

DOCKER = ("/opt/homebrew/bin/docker", "--context", "colima")
CONTAINER = "musichoarder-api-1"

def main():
    result = subprocess.run(
        [*DOCKER, "inspect", "--format", "{{json .State}}", CONTAINER],
        capture_output=True, text=True, timeout=10,
    )
    if result.returncode:
        return
    state = json.loads(result.stdout)
    health = state.get("Health", {})
    if not state["Running"] or health.get("Status") != "unhealthy":
        return
    started = datetime.fromisoformat(state["StartedAt"].replace("Z", "+00:00"))
    if health.get("FailingStreak", 0) < 12 or (datetime.now(timezone.utc) - started).total_seconds() < 600:
        return
    print(f"{datetime.now(timezone.utc).isoformat()} API unhealthy; restarting.", flush=True)
    subprocess.run([*DOCKER, "restart", "-t", "15", CONTAINER], check=True, timeout=45)

if __name__ == "__main__":
    main()
