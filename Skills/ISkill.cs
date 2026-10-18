namespace CombatAugmentationSystem.Client.Skills;

/// <summary>技能框架的最小接口：注册 / 状态 / 每帧推进 / 激活。</summary>
public interface ISkill
{
    /// <summary>稳定标识（配置与轮盘排序用）。</summary>
    string Id { get; }

    /// <summary>显示名（轮盘 / 日志用）。</summary>
    string DisplayName { get; }

    /// <summary>当前是否可激活（未激活中、无冷却）。</summary>
    bool Ready { get; }

    /// <summary>是否处于持续生效阶段。</summary>
    bool IsActive { get; }

    /// <summary>生效阶段剩余时间（秒）。</summary>
    float ActiveRemaining { get; }

    /// <summary>冷却剩余时间（秒）。</summary>
    float CooldownRemaining { get; }

    /// <summary>冷却总时长（秒，HUD 进度条用）。</summary>
    float CooldownTotal { get; }

    /// <summary>是否处于加速冷却（HUD 脉冲标记用）。</summary>
    bool IsBurning { get; }

    /// <summary>状态文本（轮盘与 HUD 显示：就绪 / 生效中 / 冷却倒计时）。</summary>
    string StatusText { get; }

    /// <summary>每帧推进（仅战局内调用）。</summary>
    void Tick(float now, float deltaTime);

    /// <summary>尝试激活；冷却中 / 资源不足时返回 false。</summary>
    bool TryActivate();

    /// <summary>战局结束 / 销毁时调用：恢复一切副作用，不进入冷却。</summary>
    void Dispose();
}
