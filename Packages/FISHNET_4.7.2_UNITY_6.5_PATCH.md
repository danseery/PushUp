# FishNet 4.7.2 Unity 6.5 Compatibility Patch

This project embeds upstream FishNet `4.7.2` at commit `de19b5d66459f60400ffd0edc443c4da173a01e7` because Unity `6000.5.7f1` promotes obsolete `GetInstanceID` and `SceneHandle` conversions to errors.

The local patch replaces only those obsolete calls with Unity 6.5 equivalents. Keep the patch small, review it whenever FishNet is upgraded, and return to the upstream Git UPM URL when an official FishNet release supports Unity 6.5 directly.
