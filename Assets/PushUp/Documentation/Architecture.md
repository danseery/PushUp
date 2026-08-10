# PushUp Runtime Architecture

```
uGUI -> SessionFlowController -> SteamSessionService (waiting/starting/playing/ending)
                           \-> SteamNetworkCoordinator -> admission -> FishNet
                                                     \-> SteamSocketsTransport/SDR
PlayerInputReader -> owner 60 Hz simulation -> client-auth transform -> remote presentation
PlayerInteraction -> host validation -> shared-world result / victim-owner impact
```

`SessionFlowController` owns mode, input gating, cursor state, UI phase, and run lifecycle. Steam lobby membership, socket connection, admission authentication, `NetworkRunState`, and the owned player are separate gates. A raw transport `Started` event is never sufficient to enter gameplay.

## Ownership

- The unit-scale player root is owner-authoritative. Its owning client (or server-local host) runs the Rigidbody motor at the serialized 60 Hz `TimeManager` tick; the server and other observers keep kinematic replicas. FishNet prediction, reconciliation, and state forwarding are disabled for players.
- A client-authoritative Rigidbody `NetworkTransform` publishes player position and rotation at 30 Hz without synchronizing scale. Live presentation yaw belongs only to local look input, is sampled into motor yaw during physics, and is never replaced by remote state or boulder auto-facing. A detached camera presenter follows the local physical root and its real knockdown rotation.
- The boulder and actors simulate only offline or on the host. Their `NetworkTransform` Rigidbody configuration makes client replicas kinematic, preventing client gravity/collision simulation from fighting host corrections.
- The boulder, powerups, anchor state, and run state are host-owned. Clients submit interaction intent through their owned `PlayerMotor`; the host checks grounded state, range, line of sight, cooldown, inventory, and capped impulse/spring values before altering shared objects.
- `RunDirector` owns run timing and delegates placement to `LevelSpawnService`. A validated `LevelLayoutSnapshot` is cached once per run, and an authoritative summit trigger replaces per-frame hierarchy scans. Replicated definitions spawn only on the host; offline mode instantiates the same prefab source without starting FishNet.
- `NetworkRunState` carries playing/completed/ending readiness, start tick, boulder anchor state, and late-join truth. Lobby metadata is discovery state, not gameplay truth.

## Movement and interaction

- Offline and network players share the Input System action reader and one velocity-limited Rigidbody motor: 10 m/s run, 15 m/s sprint, 3.3 m/s crouch, explicit acceleration/braking/reversal, slope projection, reduced air steering, and gradual preservation of external impulses.
- The capsule uses a zero-friction material, 0.20 m descending ground snap, 0.28 m static-only step assistance, and a 50-degree ordinary walking limit. Level geometry owns its visible collision surfaces; no hidden seam colliders are generated at runtime.
- Jumping uses an authored 3.0 m held arc, stronger early-release and falling gravity, plus 120 ms input buffering and coyote time. Crouching within 220 ms after takeoff adds a small vertical/forward platforming boost.
- Movement state is local to the owner. A bounded 30 Hz intent contains only the movement/stance information the host needs to apply forces to the authoritative boulder; it never drives or corrects the player's Rigidbody.
- RMB on a grounded, reachable boulder enters a dedicated push stance instead of a grab spring. The nearest surface and radial ground-plane direction are recomputed every physics tick. W aligns and pushes inward at 3.6 m/s and 650 N walking or 5.0 m/s and 1,050 N sprinting; A/D orbits the player without deliberate lateral boulder force, while S and Jump exit and latch RMB until release.
- Only the host applies continuous boulder force, derived from current player/boulder geometry. Ordinary contact retains its natural physical nudge. Neutral RMB resists the boulder's motion, while holding backward doubles that braking force and keeps the player aligned in the stance rather than retreating. LMB in the stance applies the existing host-validated 400 N s inward burst, releases the stance, and shows `PUSH` feedback. Upward contact velocity is suppressed during a grounded stance, while deliberate descending jump landings may still stand on the boulder.
- Props and both dummies retain a host-simulated local-point capped spring-damper grab. Static terrain hits retain a world anchor and brace only the player. A player-on-player grab becomes a server-validated persistent constraint evaluated by the target owner with reduced force.
- Validated punch, PUSH, and fighter impacts use a sequenced `PlayerImpactCommand`. The host applies it directly to its local player or sends one reliable targeted command to a client victim. The victim unlocks the real 78 kg root, applies the impulse at the contact point, and enters `Staggered`, `KnockedDown`, or `Recovering`; observers receive the resulting client-authoritative transform. Actor state is reliable/buffered while compact arm rotations are sent independently at 20 Hz.
- The 150 kg boulder uses continuous dynamic collision, low linear damping, moderate angular damping, moderate friction, and almost no bounce. Team assist temporarily changes its mass to 85 kg.

## Level

One scene contains base camp, teaching slope, left and right side routes, rest shelf, steep final ramp, and summit. `LevelLayout` organizes exact-transform player and world markers beneath named `SpawnGroup` parents. Markers reference reusable `SpawnDefinition` assets rather than parallel director arrays. Falling below the catch area resets the primary boulder resolved from the layout. The three prototype pickups are personal speed (20 seconds), team boulder-assist (30 seconds), and a carried anchor that any nearby player may release.

## Session and transport flow

- Host: `Main Menu -> Creating Lobby -> Host Lobby -> Start Hill -> Starting Run -> In Run`.
- Client before start: `Joining Lobby -> Client Lobby`; the client remains in the lobby UI until metadata becomes `starting`.
- Client after start: `Connecting -> Authenticating -> Waiting for run/player -> In Run`.
- Disconnects from lobby, connecting, waiting, or gameplay always clean up FishNet/Steam, restore the cursor, and show a concrete Error or Host Ended screen.

Steam operations are single-flight and generation-guarded. Friend discovery requests stale lobby metadata once and reads a cache; `LobbyDataUpdate_t` never recursively requests the same lobby. The project admission message binds Steam ID, lobby ID, build, and protocol to the transport identity before FishNet authenticates the connection.

The owned Steam transport advertises a 1199-byte FishNet MTU inside a 1200-byte wire packet, uses `NoNagle`, bounds receive draining, reports send/end failures, and reuses packet buffers. Steam callbacks run once per rendered frame.
