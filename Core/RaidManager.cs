using Comfort.Common;
using EFT;
using RZSkills.Client.Inputs;
using RZSkills.Client.Skills;
using RZSkills.Client.Visuals;
using UnityEngine;

namespace RZSkills.Client.Core;

/// <summary>战局内运行时宿主：持有 GameWorld / 玩家引用与全部技能服务。</summary>
public class RaidManager : MonoBehaviour
{
    internal static RaidManager? Instance;

    internal GameWorld World = null!;

    internal Player MainPlayer = null!;

    private ChamsService _chams = null!;
    private ScanSkill _scan = null!;
    private ShieldSkill _shield = null!;
    private BoostSkill _boost = null!;
    private WheelInputController _wheel = null!;
    private OverlayService _overlay = null!;
    private float _nextRefCheck;
    private bool _cleaned;

    internal ChamsService Chams => _chams;

    internal ScanSkill Scan => _scan;

    internal ShieldSkill Shield => _shield;

    internal BoostSkill Boost => _boost;

    internal WheelInputController Wheel => _wheel;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Plugin.Log.LogWarning("[RZSkills] duplicate RaidManager destroyed");
            Destroy(this);
            return;
        }

        Instance = this;
        TryBind();
        _chams = new ChamsService();
        _scan = new ScanSkill(this, _chams);
        _shield = new ShieldSkill(this);
        _boost = new BoostSkill(this);
        ShieldGate.Active = _shield;
        _wheel = new WheelInputController(this);
        _overlay = new OverlayService(this);

        GameWorld.OnDispose += HandleWorldDispose;
        Plugin.Log.LogInfo("[RZSkills] RaidManager attached");
    }

    private void TryBind()
    {
        if (!Singleton<GameWorld>.Instantiated)
            return;

        World = Singleton<GameWorld>.Instance;
        if (World != null)
            MainPlayer = World.MainPlayer;
    }

    private void Update()
    {
        if (World == null || MainPlayer == null)
        {
            if (Time.unscaledTime >= _nextRefCheck)
            {
                _nextRefCheck = Time.unscaledTime + 1f;
                TryBind();
            }

            return;
        }

        MaintainHook();
        MaintainCursor();

        float now = Time.unscaledTime;
        _wheel.Tick(now);
        MaintainCooldownBoostState();
        _scan.Tick(now, Time.deltaTime);
        _shield.Tick(now, Time.deltaTime);
        _boost.Tick(now, Time.deltaTime);
        _chams.Tick(now);
        EnforceLowEnergy();
    }

    /// <summary>低能量封锁：能量 ≤ 阈值时，结束所有技能（且 SkillBase 会拒绝新的激活）。</summary>
    private void EnforceLowEnergy()
    {
        if (!RzConfig.EnergyCostEnabled.Value)
            return;

        float threshold = RzConfig.EnergyLockout.Value;
        if (threshold <= 0f)
            return;

        bool low;
        try
        {
            var hc = MainPlayer.ActiveHealthController;
            low = hc != null && hc.Energy.Current <= threshold;
        }
        catch
        {
            return;
        }

        if (!low)
            return;

        _scan.ForceEnd();
        _shield.ForceEnd();
        _boost.ForceEnd();
    }

    /// <summary>
    /// 冷却加速开关的自动解除：无技能冷却 / 水分 ≤ 阈值时关闭。
    /// （水分消耗由每个冷却中的技能在 SkillBase.TickCooldown 中各自结算。）
    /// </summary>
    private void MaintainCooldownBoostState()
    {
        if (!SkillBase.CooldownBoostActive)
            return;

        bool anyCooling = _scan.CooldownRemaining > 0f || _shield.CooldownRemaining > 0f || _boost.CooldownRemaining > 0f;
        if (!anyCooling)
        {
            SkillBase.CooldownBoostActive = false;
            return;
        }

        float threshold = Mathf.Max(0f, RzConfig.WaterThreshold.Value);
        try
        {
            var hc = MainPlayer?.ActiveHealthController;
            if (hc != null && hc.Hydration.Current <= threshold)
                SkillBase.CooldownBoostActive = false;
        }
        catch
        {
        }
    }

    private void LateUpdate()
    {
        if (World == null || MainPlayer == null)
            return;

        MaintainCursor();
        _overlay.Update();
    }

    /// <summary>键盘钩子维护：仅当技能键 = CapsLock 时安装（吞掉大写切换）；其他键不需要钩子。</summary>
    private void MaintainHook()
    {
        bool wantHook = RzConfig.SkillKey.Value == KeyCode.CapsLock;
        if (wantHook)
        {
            CapsLockHook.SwallowToggle = RzConfig.SwallowCapsToggle.Value;
            if (!CapsLockHook.Installed)
                CapsLockHook.Install();
        }
        else if (CapsLockHook.Installed)
        {
            CapsLockHook.Uninstall();
        }
    }

    /// <summary>轮盘打开期间每帧把被游戏锁回去的光标压回自由态。</summary>
    private void MaintainCursor()
    {
        if (_wheel.IsOpen)
            CursorGate.Maintain();
    }

    private void HandleWorldDispose()
    {
        Cleanup();
    }

    private void Cleanup()
    {
        if (_cleaned)
            return;
        _cleaned = true;

        try
        {
            _wheel?.ForceClose();
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogError($"[RZSkills] wheel close failed: {ex}");
        }

        try
        {
            CursorGate.Exit();
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogError($"[RZSkills] cursor release failed: {ex}");
        }

        try
        {
            _scan?.Dispose();
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogError($"[RZSkills] scan dispose failed: {ex}");
        }

        try
        {
            _shield?.Dispose();
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogError($"[RZSkills] shield dispose failed: {ex}");
        }

        try
        {
            _boost?.Dispose();
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogError($"[RZSkills] boost dispose failed: {ex}");
        }

        if (ShieldGate.Active == _shield)
            ShieldGate.Active = null;

        try
        {
            _chams?.Dispose();
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogError($"[RZSkills] chams dispose failed: {ex}");
        }

        try
        {
            _overlay?.Dispose();
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogError($"[RZSkills] overlay dispose failed: {ex}");
        }

        CapsLockHook.Uninstall();
    }

    private void OnDestroy()
    {
        GameWorld.OnDispose -= HandleWorldDispose;
        Cleanup();

        if (Instance == this)
            Instance = null;

        Plugin.Log.LogInfo("[RZSkills] RaidManager destroyed");
    }
}
