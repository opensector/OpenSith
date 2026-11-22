using System.Numerics;

namespace OpenSith.Core.Cog;

/// <summary>
/// Engine abstraction interface providing COG verbs for world state query and manipulation.
/// </summary>
public interface ICogWorldContext
{
    int GetThingSector(int thingId);
    Vector3 GetThingPos(int thingId);
    void MoveToFrame(int thingId, int frame, float speed);
    bool IsThingMoving(int thingId);
    int GetCurFrame(int thingId);
    int GetLocalPlayerThing();

    void SetSectorAdjoins(int sectorId, bool visible);
    void SetSectorLight(int sectorId, float light, float delay);
    void SectorThrust(int sectorId, Vector3 dir, float speed) { }
    void SetSectorFlags(int sectorId, int flags) { }
    void ClearSectorFlags(int sectorId, int flags) { }
    int GetSectorFlags(int sectorId) => 0;
    void SetSectorTint(int sectorId, Vector3 tint) { }
    void SectorSound(int sectorId, string sound, float volume) { }
    void SetColormap(int sectorId, string colormap) { }
    int FirstThingInSector(int sectorId) => -1;
    int NextThingInSector(int prevThingId) => -1;
    int GetNumSectorVertices(int sectorId) => 0;
    Vector3 GetSectorVertexPos(int sectorId, int vertexIdx) => Vector3.Zero;

    int PlaySoundThing(string sound, int thingId, float volume, float minRad, float maxRad, int flags);

    int SendMessage(int cogId, string message);
    void Print(string text);
    float Rand();

    // Thing lifecycle & physics verbs
    int CreateThing(string template, int refThing) => -1;
    int CreateThingNR(string template, int refThing) => CreateThing(template, refThing);
    int CreateThingAtPos(string template, int sector, Vector3 pos, Vector3 pyr) => -1;
    int CreateThingAtPosNR(string template, int sector, Vector3 pos, Vector3 pyr) => CreateThingAtPos(template, sector, pos, pyr);
    void DestroyThing(int thingId) { }
    void SetLifeleft(int thingId, float seconds) { }
    float GetLifeLeft(int thingId) => 0f;
    float DamageThing(int thingId, float damage, int flags, int damager) => 0f;
    void HealThing(int thingId, float amount) { }
    float GetThingHealth(int thingId) => 0f;
    void SetThingHealth(int thingId, float health) { }
    float GetThingMaxHealth(int thingId) => 100f;
    string GetThingTemplate(int thingId) => string.Empty;
    void SetThingVel(int thingId, Vector3 vel) { }
    void AddThingVel(int thingId, Vector3 vel) { }
    Vector3 GetThingVel(int thingId) => Vector3.Zero;
    void SetThingRotVel(int thingId, Vector3 rotVel) { }
    Vector3 GetThingRotVel(int thingId) => Vector3.Zero;
    void ApplyForce(int thingId, Vector3 force) { }
    void StopThing(int thingId) { }
    void ThingLight(int thingId, float light, float fadeTime) { }
    float GetThingLight(int thingId) => 0f;
    int PlayKey(int thingId, string keyframe, int priority, int flags) => 0;
    void StopKey(int thingId, int track, float fadeTime) { }
    int PlayMode(int thingId, int mode) => -1;
    int GetThingFlags(int thingId) => 0;
    void SetThingFlags(int thingId, int flags) { }
    void ClearThingFlags(int thingId, int flags) { }
    int GetActorFlags(int thingId) => 0;
    void SetActorFlags(int thingId, int flags) { }
    void ClearActorFlags(int thingId, int flags) { }
    int GetPhysicsFlags(int thingId) => 0;
    void SetPhysicsFlags(int thingId, int flags) { }
    void ClearPhysicsFlags(int thingId, int flags) { }
    int GetItemFlags(int thingId) => 0;
    int GetAttachFlags(int thingId) => 0;
    void SetThingAttachFlags(int thingId, int flags) { }
    void AttachThingToThingEx(int thingId, int attachToThingId, int attachFlags) { }
    void DetachThing(int thingId) { }
    void RotatePivot(int thingId, int frameIdx, float time) { }
    void JumpToFrame(int thingId, int frameIdx, int sectorId) { }
    void SkipToFrame(int thingId, int frameIdx, float speed) { }
    void PathMovePause(int thingId) { }
    void PathMoveResume(int thingId) { }
    int GetGoalFrame(int thingId) => -1;
    void TeleportThing(int thingId, int destThingId) { }
    void SetThingTimer(int thingId, float seconds) { }
    float GetThingMass(int thingId) => 0f;
    void SetThingMass(int thingId, float mass) { }
    int ThingLightAnim(int thingId, float startLight, float endLight, float period) => -1;
    int IsThingVisible(int thingId) => 0;
    int GetThingClassCog(int thingId) => -1;
    int GetThingGeoMode(int thingId) => 0;
    void SetThingCurGeoMode(int thingId, int geoMode) { }
    int GetThingParent(int thingId) => -1;
    int GetThingSignature(int thingId) => 0;
    Vector3 GetThingThrust(int thingId) => Vector3.Zero;
    int GetThingType(int thingId) => 0;
    float GetThingUserData(int thingId) => 0f;
    void SetThingUserData(int thingId, float data) { }
    void SetThingLook(int thingId, Vector3 lookVec) { }
    Vector3 GetThingLVec(int thingId) => Vector3.Zero;
    void CaptureThing(int thingId, int cogId) { }
    void SetThingPulse(int thingId, float interval) { }
    void SetThingCollideSize(int thingId, float size) { }
    void SetCollideType(int thingId, int collideType) { }
    void SetActorExtraSpeed(int thingId, float extraSpeed) { }
    void SetActorWeapon(int thingId, int slot, string weaponTemplate) { }
    void SetArmedMode(int thingId, int mode) { }
    void SetParticleGrowthSpeed(int thingId, float speed) { }
    void ParseArg(int thingId, string argStr) { }
    int GetCurSector() => -1;

    // Inventory, weapon & goal verbs
    float ChangeInv(int thingId, int bin, float delta) => 0f;
    float GetInv(int thingId, int bin) => 0f;
    void SetInv(int thingId, int bin, float val) { }
    float GetInvMax(int thingId, int bin) => 0f;
    void ActivateBin(int playerThing, float delay, int binIdx) { }
    float DeactivateBin(int playerThing, int binIdx) => 0f;
    int IsInvActivated(int playerThing, int binIdx) => 0;
    void SetInvActivated(int playerThing, int binIdx, int activated) { }
    void SetInvAvailable(int playerThing, int binIdx, int available) { }
    int IsInvAvailable(int playerThing, int binIdx) => 0;
    void SetBinWait(int playerThing, int binIdx, float wait) { }
    void SetGoalFlags(int thingId, int goalIdx, int flags) { }
    void ClearGoalFlags(int thingId, int goalIdx, int flags) { }
    int GetGoalFlags(int thingId, int goalIdx) => 0;
    void TakeItem(int itemThingId, int playerThingId) { }
    void AddDynamicTint(int thingId, float r, float g, float b) { }
    void AddDynamicAdd(int thingId, float r, float g, float b) { }
    int GetAutoReload() => 0;
    int GetAutoPickup() => 0;
    int GetAutoSwitch() => 0;
    int AutoSelectWeapon(int thingId, int mode) => 1;
    void SelectWeapon(int thingId, int weapon) { }
    int GetCurWeapon(int thingId) => 1;
    int GetCurWeaponMode() => 0;
    float GetWeaponPriority(int playerThing, int binIdx, int mode) => 0f;
    void SetWeaponFlags(int flags) { }
    void SetWeaponFlags(int thingId, int flags) => SetWeaponFlags(flags);
    void ChangeFireRate(int weaponThing, float fireRate) { }
    int CreateBackpack(int playerThingId) => -1;
    void PickupBackpack(int playerThingId, int backpackThingId) { }
    void SetCurWeapon(int playerThing, int binIdx) { }
    void SetCurInvWeapon(int thingId, int weapon) { }
    int GetCurInvWeapon(int thingId) => -1;
    void ActivateWeapon(int weaponThing, float fireRate, int mode) { }
    float DeactivateWeapon(int weaponThing, int mode) => 0f;
    int FireProjectile(int shooterThing, string projectileTemplate, string fireSound, int mode, Vector3 fireOffset, Vector3 aimError, float scale, int scaleFlags, float autoaimFov, float autoaimMaxDist) => -1;
    void SetFireWait(int thingId, float wait) { }
    void SetMountWait(int thingId, float wait) { }

    // AI, targeting & joint verbs
    void AmputateJoint(int thingId, int jointIdx) { }
    void AISetClass(int thingId, string aiClass) { }
    void AIFlee(int thingId, int targetThing) { }
    void AISetLookFrame(int thingId, int frame) { }
    void AISetMoveFrame(int thingId, int frame) { }
    void AISetLookPos(int thingId, Vector3 pos) { }
    void AISetMovePos(int thingId, Vector3 pos) { }
    void AISetMoveThing(int thingId, int targetThingId) { }
    void AISetFireTarget(int thingId, int targetThingId) { }
    void AISetMoveSpeed(int thingId, float speed) { }
    void AIJump(int thingId, int destThingId, float speed) { }
    int AiGetMode(int thingId) => -1;
    void AiSetMode(int thingId, int mode) { }
    void AiClearMode(int thingId, int mode) { }
    int GetActorWeapon(int thingId, int slot) => -1;
    float SkillTarget(int targetThing, int sourceThing, float param0, float param1) => -1f;
    int FirstThingInView(int viewerThing, float fov, float maxDist, int typeMask) => -1;
    int NextThingInView() => -1;
    int HasLOS(int thingA, int thingB) => 0;
    float ThingViewDot(int viewerThing, int targetThing) => -1000f;

    // Surface, camera, effects & audio verbs
    void SetFaceGeoMode(int surfaceId, int geoMode) { }
    int GetFaceGeoMode(int surfaceId) => 0;
    void SetFaceType(int surfaceId, int faceFlags) { }
    void SetSurfaceFlags(int surfaceId, int flags) { }
    void ClearSurfaceFlags(int surfaceId, int flags) { }
    int GetSurfaceFlags(int surfaceId) => 0;
    void SetSurfaceMat(int surfaceId, string materialName) { }
    void SetAdjoinFlags(int surfaceId, int flags) { }
    void ClearAdjoinFlags(int surfaceId, int flags) { }
    int GetAdjoinFlags(int surfaceId) => 0;
    int GetSurfaceSector(int surfaceId) => -1;
    Vector3 GetSurfaceNormal(int surfaceId) => Vector3.Zero;
    int GetNumSurfaceVertices(int surfaceId) => 0;
    Vector3 GetSurfaceVertexPos(int surfaceId, int vertexIdx) => Vector3.Zero;
    int GetSurfaceAnim(int surfaceId) => -1;
    int SurfaceLightAnim(int surfaceId, float startLight, float endLight, float period) => -1;
    void StopAnim(int animId) { }
    void SetSurfaceCel(int surfaceId, int cel) { }
    int GetSurfaceCel(int surfaceId) => 0;
    void SlideSurface(int surfaceId, Vector3 dir, float speed) { }
    void SlideCeilingSky(float uSpeed, float vSpeed) { }
    int SurfaceAnim(int surfaceId, float fps, int flags) => 0;
    void StopSurfaceAnim(int surfaceId) { }
    int MaterialAnim(string material, float fps, int flags) => 0;
    Vector3 SurfaceCenter(int surfaceId) => Vector3.Zero;
    int PlaySoundLocal(string sound, float volume, float pan, int flags) => 0;
    int PlaySoundPos(string sound, Vector3 pos, float volume, float minRad, float maxRad, int flags) => 0;
    void StopSound(int channel, float fadeOut) { }
    void ChangeSoundPitch(int channel, float pitch, float changeTime) { }
    void ChangeSoundVol(int channel, float volume, float fadeTime) { }
    float GetSoundLen(string sound) => 0f;
    float GetKeyLen(string keyframe) => 0f;
    void CycleCamera() { }
    int GetCurrentCamera() => 0;
    void SetCurrentCamera(int camIdx) { }
    void SetCameraFocus(int camIdx, int thingId) { }
    int GetPrimaryFocus(int cameraIdx) => -1;
    void SetMusicVol(float vol) { }
    void PlaySong(int startTrack, int endTrack, int loopTo) { }
    void EnableIRMode(float intensity, float fade) { }
    void DisableIRMode() { }
    int NewColorEffect(int filterR, int filterG, int filterB, float tintR, float tintG, float tintB, int addR, int addG, int addB, float fade) => 0;
    void FreeColorEffect(int effectId) { }
    void SetMapModeFlags(int flags) { }
    void ClearMapModeFlags(int flags) { }
    int GetMapModeFlags() => 0;
    void SetPovShake(Vector3 posOffset, Vector3 rotOffset, float duration, float intensity) { }

    // Level, JK saber/HUD & VM context verbs
    void JkEndLevel(int success) { }
    void AutosaveGame() { }
    void JkPrintUniString(int player, int strId) { }
    void JkSetFlags(int thingId, int flags) { }
    int JkGetFlags(int thingId) => 0;
    void JkClearFlags(int thingId, int flags) { }
    void JkSetWeaponMesh(int thingId, string model) { }
    string JkGetWeaponMesh(int thingId) => string.Empty;
    void JkEnableSaber(int thingId, float damage, float collideLength, float unknown) { }
    void JkDisableSaber(int thingId) { }
    void JkSetSaberInfo(int thingId, string sideMat, string tipMat, float baseRad, float tipRad, float length, string wallSparks, string bloodSparks, string saberSparks) { }
    void JkSetPersuasionInfo(int thingId, int maxTwinkles, int spawnRate) { }
    void JkSetPovModel(int thingId, string model) { }
    int JkPlayPovKey(int thingId, string keyframe, int priority, int flags) => -1;
    void JkStopPovKey(int thingId, int track, float fadeTime) { }
    void JkSetWaggle(int thingId, Vector3 moveVec, float speed) { }
    int JkGetSaberCam() => 0;
    void JkSetTarget(int targetThingId) { }
    void JkEndTarget() { }
    void JkSetTargetColors(int r, int g, int b) { }
    void JkBeginCutscene() { }
    void JkEndCutscene() { }
    void JkSetSuperFlags(int flags) { }
    void JkClearSuperFlags(int flags) { }
    int JkGetSuperFlags() => 0;
    int JkGetChoice() => 1;
    void JkSetInvulnerable(int thingId, int invulnerable) { }
    void SyncSurface(int surfaceId) { }
    void SyncThingPos(int thingId) { }
    void AssignWeapon(int playerThing, int binIdx) { }
    string LoadSound(string soundName) => soundName ?? string.Empty;
    string LoadTemplate(string templateName) => templateName ?? string.Empty;
    int GetMajorMode() => 0;
    int GetMajorMode(int thingId) => GetMajorMode();
    int GetDifficulty() => 1;
    int IsMulti() => 0;
    int IsServer() => 1;
    int GetNumPlayers() => 1;
    int GetPlayerThing(int playerIdx) => GetLocalPlayerThing();
    int GetPlayerTeam(int playerThing) => 0;
    void SetPlayerTeam(int playerThing, int team) { }
    int GetPlayerScore(int playerThing) => 0;
    void SetPlayerScore(int playerThing, int score) { }
    int GetPlayerKills(int playerThing) => 0;
    int GetPlayerKilled(int playerThing) => 0;
    int GetPlayerSuicides(int playerThing) => 0;
    int GetSenderId() => -1;
    int GetSenderType() => 0;
    int GetSourceRef() => -1;
    float GetLevelTime() => 0f;
}
