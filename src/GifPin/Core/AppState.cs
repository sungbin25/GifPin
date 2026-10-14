using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace GifPin.Core;

/// <summary>라이브러리에 등록된 사진 한 장.</summary>
public sealed class PhotoItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>파일 경로. 데이터 폴더 기준 상대 경로이거나 절대 경로.</summary>
    public string File { get; set; } = "";
    /// <summary>처음 추가할 때의 원본 경로 (중복 추가 방지용).</summary>
    public string? SourcePath { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int FrameCount { get; set; }
    public long FileSize { get; set; }
    public bool Favorite { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    /// <summary>배경을 지운 사진이면 원본 사진의 Id.</summary>
    public string? SourceId { get; set; }
    public BgRemoveSettings? BgRemoval { get; set; }
    /// <summary>도트 그래픽: 확대할 때 흐리지 않고 픽셀을 그대로 키운다.</summary>
    public bool Pixelated { get; set; }
}

public sealed class BgRemoveSettings
{
    public string Color { get; set; } = "#FFFFFF";
    public int Tolerance { get; set; } = 15;
    public bool ConnectedOnly { get; set; } = true;
    public int Refine { get; set; } = 1;
}

/// <summary>지금 화면에 떠 있는 GIF 하나.</summary>
public sealed class ActiveItem
{
    public string Id { get; set; } = "";
    public string PhotoId { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public double Scale { get; set; } = 1;
    public double Opacity { get; set; } = 1;
    public double Speed { get; set; } = 1;
    public bool FlipX { get; set; }
    public bool ClickThrough { get; set; } = true;
    public bool Visible { get; set; } = true;
    /// <summary>움직임: none | walk | bounce | fall (Motion 참고)</summary>
    public string MotionMode { get; set; } = Motion.None;
    /// <summary>움직이는 속도 (px/초)</summary>
    public double MotionSpeed { get; set; } = Motion.DefaultSpeed;
    /// <summary>움직이는 방향을 바라보도록 좌우 자동 반전</summary>
    public bool MotionFaceDirection { get; set; } = true;
    /// <summary>걷기·떨어지기: 켜면 작업표시줄 위가 바닥, 끄면 배치한 높이가 땅 (그 높이에서 수평 이동·점프)</summary>
    public bool MotionGravity { get; set; } = true;
    /// <summary>표시 위치: top = 모든 프로그램 위, desktop = 바탕화면 (모든 프로그램 아래)</summary>
    public string Layer { get; set; } = Layers.Top;
    /// <summary>떨어지기: 바닥에 멈춘 뒤 주기적으로 뛰어오름</summary>
    public bool JumpEnabled { get; set; } = true;
    /// <summary>점프 높이 (px)</summary>
    public double JumpHeight { get; set; } = Motion.DefaultJumpHeight;
    /// <summary>바닥에 내려온 뒤 다음 점프까지 (초)</summary>
    public double JumpInterval { get; set; } = Motion.DefaultJumpInterval;
    /// <summary>점프 각도 (도). 0 = 수직, + 오른쪽 / − 왼쪽. 벽에 부딪히면 방향을 바꿈</summary>
    public double JumpAngle { get; set; } = Motion.DefaultJumpAngle;
    /// <summary>점프 방향을 매번 랜덤으로 (−JumpAngleRange ~ +JumpAngleRange)</summary>
    public bool JumpAngleRandom { get; set; }
    public double JumpAngleRange { get; set; } = Motion.DefaultJumpAngleRange;
    /// <summary>점프 높이를 매번 랜덤으로 (JumpHeightMin ~ JumpHeightMax)</summary>
    public bool JumpHeightRandom { get; set; }
    public double JumpHeightMin { get; set; } = Motion.DefaultJumpHeightMin;
    public double JumpHeightMax { get; set; } = Motion.DefaultJumpHeightMax;

    /// <summary>미끄러짐: 멈춰 있으면 일정(또는 랜덤) 간격으로 저절로 밀려남</summary>
    public bool SlideAuto { get; set; }
    /// <summary>미는 방향: 랜덤 또는 PushAngle (0° 오른쪽, 90° 위, 180° 왼쪽, 270° 아래)</summary>
    public bool PushDirRandom { get; set; } = true;
    public double PushAngle { get; set; }
    /// <summary>미는 힘(처음 속도 px/s): 랜덤(PushForceMin~Max) 또는 PushForce</summary>
    public bool PushForceRandom { get; set; } = true;
    public double PushForce { get; set; } = Motion.DefaultPushForce;
    public double PushForceMin { get; set; } = Motion.DefaultPushForceMin;
    public double PushForceMax { get; set; } = Motion.DefaultPushForceMax;
    /// <summary>멈춘 뒤 다시 밀기까지(초): 랜덤(PushIntervalMin~Max) 또는 PushInterval</summary>
    public bool PushIntervalRandom { get; set; } = true;
    public double PushInterval { get; set; } = Motion.DefaultPushInterval;
    public double PushIntervalMin { get; set; } = Motion.DefaultPushIntervalMin;
    public double PushIntervalMax { get; set; } = Motion.DefaultPushIntervalMax;

    // ── 효과 레이어 (Fx 참고) ──────────────────────────────
    /// <summary>둥실둥실: 위아래로 떠다님 (진폭 px, 주기 초)</summary>
    public bool FxFloat { get; set; }
    public double FxFloatAmp { get; set; } = 10;
    public double FxFloatPeriod { get; set; } = 2.5;
    /// <summary>숨쉬기: 크기가 커졌다 작아짐 (%, 초)</summary>
    public bool FxBreathe { get; set; }
    public double FxBreatheAmount { get; set; } = 6;
    public double FxBreathePeriod { get; set; } = 3;
    /// <summary>흔들림: 좌우로 기울어짐 (각도, 초)</summary>
    public bool FxSway { get; set; }
    public double FxSwayAngle { get; set; } = 8;
    public double FxSwayPeriod { get; set; } = 2;
    /// <summary>회전: 계속 돔 (도/초, 방향)</summary>
    public bool FxSpin { get; set; }
    public double FxSpinSpeed { get; set; } = 90;
    public bool FxSpinClockwise { get; set; } = true;
    /// <summary>깜빡임: 투명도가 오르내림 (최저 %, 초)</summary>
    public bool FxBlink { get; set; }
    public double FxBlinkMin { get; set; } = 40;
    public double FxBlinkPeriod { get; set; } = 1.5;
    /// <summary>찌그러짐: 착지·점프·벽에 부딪힐 때 말랑하게 (%)</summary>
    public bool FxSquash { get; set; }
    public double FxSquashAmount { get; set; } = 25;
    /// <summary>착지 먼지</summary>
    public bool FxDust { get; set; }
    /// <summary>잔상: 움직일 때 뒤에 남는 그림 (개수, 첫 잔상 투명도 %)</summary>
    public bool FxTrail { get; set; }
    public double FxTrailCount { get; set; } = 5;
    public double FxTrailOpacity { get; set; } = 40;
    /// <summary>잔상 사이 간격 (px): 이만큼 움직일 때마다 잔상 하나</summary>
    public double FxTrailSpacing { get; set; } = 24;
    /// <summary>바닥 그림자 (진하기 %, 크기 %)</summary>
    public bool FxShadow { get; set; }
    public double FxShadowOpacity { get; set; } = 40;
    public double FxShadowSize { get; set; } = 80;
    /// <summary>외곽 발광 (색, 크기 px, 세기 %, 맥동)</summary>
    public bool FxGlow { get; set; }
    public string FxGlowColor { get; set; } = "#FFD54F";
    public double FxGlowSize { get; set; } = 10;
    public double FxGlowStrength { get; set; } = 70;
    public bool FxGlowPulse { get; set; }
    /// <summary>파티클 (모양, 나오는 때, 움직임, 개수/초, 크기, 수명, 색)</summary>
    public bool FxParticles { get; set; }
    public string FxParticleKind { get; set; } = Fx.Sparkle;
    public string FxParticleMode { get; set; } = Fx.Always;
    public string FxParticleStyle { get; set; } = Fx.Rise;
    public double FxParticleRate { get; set; } = 6;
    public double FxParticleSize { get; set; } = 14;
    public double FxParticleLife { get; set; } = 1.6;
    public string FxParticleColor { get; set; } = Fx.Rainbow;
    /// <summary>파티클로 쓸 내 이미지 (라이브러리 사진 id, 쉼표로 구분. 여러 장이면 랜덤)</summary>
    public string FxParticleImages { get; set; } = "";
    /// <summary>내 이미지 파티클: 원래 색 그대로 (끄면 고른 색으로 물들임)</summary>
    public bool FxParticleKeepColors { get; set; } = true;
    /// <summary>파티클이 GIF를 따라 함께 움직임 (끄면 생긴 자리에 남아 꼬리처럼 보임)</summary>
    public bool FxParticleFollow { get; set; } = true;
    /// <summary>파티클이 돌면서 날림</summary>
    public bool FxParticleRotate { get; set; } = true;
    /// <summary>GIF 파티클: 파티클마다 애니메이션 재생 (CPU를 더 씀)</summary>
    public bool FxParticleAnimate { get; set; }

    /// <summary>미끄러짐 마찰 (1~10). 높을수록 빨리 멈춤</summary>
    public double SlideFriction { get; set; } = Motion.DefaultFriction;

    public ActiveItem Clone() => (ActiveItem)MemberwiseClone();

    /// <summary>"모양" 설정이 아닌 것: 어느 사진·어디·얼마나 크게·보이는지 (설정 복사·기본값·프리셋에서 제외).</summary>
    private static readonly HashSet<string> PlacementProps = new() { nameof(Id), nameof(PhotoId), nameof(X), nameof(Y), nameof(Scale), nameof(Visible) };

    private static readonly System.Reflection.PropertyInfo[] StyleProps = typeof(ActiveItem).GetProperties()
        .Where(p => p.CanRead && p.CanWrite && !PlacementProps.Contains(p.Name)).ToArray();

    /// <summary>
    /// 다른 GIF의 설정(움직임·효과·표시 방식)을 가져옴. 위치·크기·사진은 그대로.
    /// effectsOnly면 효과 레이어(Fx…)만.
    /// </summary>
    public void CopyStyleFrom(ActiveItem src, bool effectsOnly = false)
    {
        foreach (var p in StyleProps)
            if (!effectsOnly || p.Name.StartsWith("Fx", StringComparison.Ordinal)) p.SetValue(this, p.GetValue(src));
        Normalize();
    }

    /// <summary>켜져 있는 효과 이름들 (요약 표시용).</summary>
    public int EnabledEffectCount() => new[] { FxFloat, FxBreathe, FxSway, FxSpin, FxBlink, FxSquash, FxDust, FxTrail, FxShadow, FxGlow, FxParticles }.Count(b => b);

    /// <summary>
    /// 모든 값을 허용 범위로 바로잡음. 화면에서 바꿀 때뿐 아니라 저장 파일을 불러올 때도 실행
    /// (파일이 손상되거나 손으로 고친 값 때문에 매 프레임 오류·과부하가 나지 않게).
    /// </summary>
    public void Normalize()
    {
        var a = this;
        if (!double.IsFinite(a.Scale)) a.Scale = 1;
        if (!double.IsFinite(a.Opacity)) a.Opacity = 1;
        a.Scale = Math.Round(Math.Clamp(a.Scale, 0.1, 4.0), 3);
        a.Opacity = Math.Clamp(a.Opacity, 0.1, 1.0);
        a.MotionMode = Motion.Normalize(a.MotionMode);
        a.MotionSpeed = Math.Clamp(a.MotionSpeed, Motion.MinSpeed, Motion.MaxSpeed);
        a.JumpHeight = Math.Clamp(a.JumpHeight, Motion.MinJumpHeight, Motion.MaxJumpHeight);
        a.JumpInterval = Math.Clamp(a.JumpInterval, Motion.MinJumpInterval, Motion.MaxJumpInterval);
        a.JumpAngle = Math.Clamp(a.JumpAngle, -Motion.MaxJumpAngle, Motion.MaxJumpAngle);
        a.SlideFriction = Math.Clamp(a.SlideFriction, Motion.MinFriction, Motion.MaxFriction);
        a.Layer = Layers.Normalize(a.Layer);
        a.JumpAngleRange = Math.Clamp(a.JumpAngleRange, 0, Motion.MaxJumpAngle);
        a.JumpHeightMin = Math.Clamp(a.JumpHeightMin, Motion.MinJumpHeight, Motion.MaxJumpHeight);
        a.JumpHeightMax = Math.Clamp(a.JumpHeightMax, a.JumpHeightMin, Motion.MaxJumpHeight);
        a.PushAngle = ((a.PushAngle % 360) + 360) % 360;
        a.PushForce = Math.Clamp(a.PushForce, Motion.MinPushForce, Motion.MaxPushForce);
        a.PushForceMin = Math.Clamp(a.PushForceMin, Motion.MinPushForce, Motion.MaxPushForce);
        a.PushForceMax = Math.Clamp(a.PushForceMax, a.PushForceMin, Motion.MaxPushForce);
        a.PushInterval = Math.Clamp(a.PushInterval, Motion.MinPushInterval, Motion.MaxPushInterval);
        a.PushIntervalMin = Math.Clamp(a.PushIntervalMin, Motion.MinPushInterval, Motion.MaxPushInterval);
        a.PushIntervalMax = Math.Clamp(a.PushIntervalMax, a.PushIntervalMin, Motion.MaxPushInterval);
        Fx.Normalize(a);
        a.Speed = double.IsFinite(a.Speed) ? Math.Clamp(a.Speed, 0.25, 4.0) : 1;
        a.FxParticleImages ??= "";
    }
}

public sealed class WindowBounds
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

public sealed class AppSettings
{
    /// <summary>UI 언어: "en"(기본) 또는 "ko"</summary>
    public string Language { get; set; } = "en";
    public bool StartWithWindows { get; set; }
    public bool OpenWindowOnStart { get; set; }
    public bool CopyOnAdd { get; set; } = true;
    public bool AllHidden { get; set; }
    /// <summary>움직이는 GIF를 이만큼(ms) 누르고 있으면 멈추고 잡음</summary>
    public int HoldToGrabMs { get; set; } = 200;
    /// <summary>잡았다 놓은 뒤 이만큼(ms) 지나면 다시 움직임</summary>
    public int ResumeDelayMs { get; set; } = 500;
    /// <summary>효과 초당 프레임 (30 또는 60)</summary>
    public int EffectsFps { get; set; } = 60;
    /// <summary>전체 화면 게임·발표 중에는 효과 멈춤 (기본: 끔 → 전체 화면에서도 효과 동작)</summary>
    public bool PauseEffectsFullscreen { get; set; }
    /// <summary>다른 프로그램이 CPU를 많이 쓰면(게임 등) 파티클을 자동으로 끔</summary>
    public bool AutoLightEffects { get; set; } = true;
    /// <summary>이 CPU 사용률(%, GifPin 제외) 이상이 몇 초 이어지면 절약</summary>
    public int AutoLightCpuPercent { get; set; } = 80;
    /// <summary>설정 형식 버전 (기본값이 바뀐 항목을 한 번만 옮기기 위해)</summary>
    public int SettingsVersion { get; set; } // 예전 파일엔 없으므로 0 → 불러올 때 옮긴 뒤 CurrentVersion
    public const int CurrentVersion = 3;
    /// <summary>배터리로 쓸 때 효과 멈춤</summary>
    public bool PauseEffectsOnBattery { get; set; }
    public bool TrayNoticeShown { get; set; }
    public WindowBounds? Window { get; set; }

    /// <summary>화면 테마: system | dark | light | midnight | mint | neon | xp | classic</summary>
    public string Theme { get; set; } = "dark";
    /// <summary>설치 프로그램에서 고른 언어를 마지막으로 적용한 설치 표시 (같은 설치는 한 번만 적용)</summary>
    public string? AppliedInstallStamp { get; set; }

    /// <summary>화면 녹화·공유·캡처에 GIF가 찍히지 않게 (내 화면에만 보임)</summary>
    public bool HideFromCapture { get; set; }
    /// <summary>클릭 통과 GIF 위에 마우스를 올리면 흐려짐</summary>
    public bool HoverFade { get; set; }
    /// <summary>흐려졌을 때 남는 진하기 (%)</summary>
    public int HoverFadeOpacity { get; set; } = 25;
    /// <summary>모든 효과 끄기 (단축키·트레이로 켜고 끔)</summary>
    public bool EffectsOff { get; set; }
    /// <summary>전역 단축키: 동작 → "Ctrl+Alt+E" (빈 글자 = 없음)</summary>
    public Dictionary<string, string> Hotkeys { get; set; } = new();

    /// <summary>새 GIF를 화면에 올릴 때의 크기: 긴 변이 이보다 크면 이만큼으로 줄임 (px)</summary>
    public int NewItemSize { get; set; } = 400;
    /// <summary>새 GIF에 적용할 설정 (움직임·효과·층·클릭 통과 등). null = 기본값</summary>
    public ActiveItem? NewItemTemplate { get; set; }

    /// <summary>이 프로그램이 앞에 있거나(또는 실행 중이면) GIF를 모두 숨김 (실행 파일 이름, 예: "POWERPNT.EXE")</summary>
    public List<string> HideForApps { get; set; } = new();
    /// <summary>false = 그 프로그램이 앞에 있을 때만, true = 실행 중이기만 해도</summary>
    public bool HideForAppsWhileRunning { get; set; }

    /// <summary>새 버전이 나왔는지 하루 한 번 확인</summary>
    public bool CheckUpdates { get; set; } = true;
    public DateTimeOffset? LastUpdateCheck { get; set; }
    /// <summary>이미 알린 새 버전 (같은 버전은 다시 알리지 않음)</summary>
    public string? NotifiedVersion { get; set; }
    /// <summary>Windows 시작 시 자동 실행할 때 이만큼(초) 기다렸다가 GIF를 띄움</summary>
    public int AutostartDelaySec { get; set; }

    // ── 성능 ──
    /// <summary>성능 모드: saver | balanced | quality | custom (PerfModes 참고)</summary>
    public string PerfMode { get; set; } = PerfModes.Balanced;
    /// <summary>모든 GIF를 합친 파티클 최대 수</summary>
    public int MaxParticlesTotal { get; set; } = 400;
    /// <summary>GIF 장면을 다시 그리는 최대 횟수 (초당, 0 = 제한 없음 → 효과 프레임과 같음)</summary>
    public int GifFpsCap { get; set; }
    /// <summary>확대·축소 품질: low | medium | high</summary>
    public string RenderQuality { get; set; } = "medium";
    /// <summary>GIF 하나를 디코딩해 둘 때 쓰는 최대 메모리 (MB)</summary>
    public int MemoryBudgetMB { get; set; } = 384;
    /// <summary>숨긴 GIF는 잠시 뒤 메모리에서 내림 (다시 보이면 새로 불러옴)</summary>
    public bool UnloadHidden { get; set; } = true;
    /// <summary>바탕화면 층 GIF가 다른 창에 완전히 가려지면 재생·효과를 멈춤</summary>
    public bool PauseOccluded { get; set; } = true;
    /// <summary>화면 잠금·화면 보호기·모니터 꺼짐 중에는 모두 멈춤</summary>
    public bool PauseWhenLocked { get; set; } = true;
    /// <summary>라이브러리에서 마우스를 올리거나 선택했을 때 GIF를 움직여서 미리보기</summary>
    public bool LibraryPreview { get; set; } = true;

    /// <summary>"설정 초기화": 언어·창 위치·자동 실행처럼 사용자가 의도해서 바꾼 앱 정보는 남기고 나머지를 기본값으로.</summary>
    public AppSettings ResetKeepingBasics() => new()
    {
        Language = Language,
        Theme = Theme,
        AppliedInstallStamp = AppliedInstallStamp,
        StartWithWindows = StartWithWindows,
        AllHidden = false,
        SettingsVersion = SettingsVersion,
        TrayNoticeShown = TrayNoticeShown,
        Window = Window,
        Hotkeys = Core.Hotkeys.Normalize(null),
    };

    /// <summary>저장된 값을 허용 범위로 바로잡음.</summary>
    public void Normalize()
    {
        AutostartDelaySec = Math.Clamp(AutostartDelaySec, 0, 300);
        Theme = Theme is "system" or "dark" or "light" or "midnight" or "mint" or "neon" or "xp" or "classic" ? Theme : "dark";
        MaxParticlesTotal = Math.Clamp(MaxParticlesTotal, 20, 3000);
        GifFpsCap = GifFpsCap <= 0 ? 0 : Math.Clamp(GifFpsCap, 10, 60);
        RenderQuality = RenderQuality is "low" or "high" ? RenderQuality : "medium";
        MemoryBudgetMB = Math.Clamp(MemoryBudgetMB, 64, 2048);
        PerfMode = PerfModes.Normalize(PerfMode);
        HoverFadeOpacity = Math.Clamp(HoverFadeOpacity, 0, 90);
        Hotkeys = Core.Hotkeys.Normalize(Hotkeys);
        NewItemSize = Math.Clamp(NewItemSize, 32, 2000);
        NewItemTemplate?.Normalize();
        HideForApps = (HideForApps ?? new()).Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(AppRules.NormalizeName).Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToList();
    }
}

/// <summary>이름 붙여 저장한 효과 조합.</summary>
public sealed class EffectPreset
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>효과 값 (Fx… 속성만 씀)</summary>
    public ActiveItem Values { get; set; } = new();
    /// <summary>앱에 들어 있는 기본 프리셋 (저장되지 않음, 지울 수 없음)</summary>
    [JsonIgnore] public bool BuiltIn { get; set; }

    /// <summary>기본 프리셋: 이름은 문구 키(preset.*)로 현재 언어에 맞춰 표시.</summary>
    public static IReadOnlyList<EffectPreset> BuiltIns()
    {
        EffectPreset Make(string key, Action<ActiveItem> set)
        {
            var v = new ActiveItem();
            set(v);
            v.Normalize();
            return new EffectPreset { Id = "builtin:" + key, Name = Loc.T("preset." + key), Values = v, BuiltIn = true };
        }
        return new[]
        {
            Make("sparkle", v => { v.FxParticles = true; v.FxParticleKind = Fx.Sparkle; v.FxParticleStyle = Fx.Rise; v.FxParticleRate = 5; v.FxGlow = true; v.FxGlowColor = "#FFF3B0"; v.FxGlowSize = 8; v.FxGlowStrength = 55; v.FxGlowPulse = true; }),
            Make("love", v => { v.FxParticles = true; v.FxParticleKind = Fx.Heart; v.FxParticleColor = "#FF6FA5"; v.FxParticleRate = 3; v.FxBreathe = true; v.FxBreatheAmount = 5; v.FxBreathePeriod = 1.2; }),
            Make("ghost", v => { v.FxFloat = true; v.FxFloatAmp = 12; v.FxBlink = true; v.FxBlinkMin = 55; v.FxBlinkPeriod = 3; v.FxTrail = true; v.FxTrailCount = 4; v.FxTrailOpacity = 30; }),
            Make("bouncy", v => { v.FxSquash = true; v.FxSquashAmount = 35; v.FxDust = true; v.FxShadow = true; }),
            Make("dreamy", v => { v.FxFloat = true; v.FxFloatAmp = 8; v.FxFloatPeriod = 3.5; v.FxShadow = true; v.FxParticles = true; v.FxParticleKind = Fx.Dot; v.FxParticleRate = 4; v.FxParticleColor = Fx.Rainbow; }),
        };
    }
}

/// <summary>저장해 둔 화면 배치 (장면).</summary>
public sealed class Scene
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<ActiveItem> Items { get; set; } = new();
    /// <summary>매일 이 시각("HH:mm")에 자동으로 적용 (없으면 수동만)</summary>
    public string? AutoAt { get; set; }
    public DateTimeOffset SavedAt { get; set; }

    /// <summary>"7:5", "07:05" → "07:05". 틀린 형식이면 null.</summary>
    public static string? NormalizeTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Trim().Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int h) || !int.TryParse(parts[1], out int m)) return null;
        if (h < 0 || h > 23 || m < 0 || m > 59) return null;
        return $"{h:00}:{m:00}";
    }
}

public sealed class AppState
{
    public int Version { get; set; } = 2;
    public List<PhotoItem> Library { get; set; } = new();
    public List<ActiveItem> Active { get; set; } = new();
    public AppSettings Settings { get; set; } = new();
    public List<Scene> Scenes { get; set; } = new();
    public List<EffectPreset> EffectPresets { get; set; } = new();
    /// <summary>마지막으로 적용한 장면 (다음 장면 단축키용)</summary>
    public string? CurrentSceneId { get; set; }

    public PhotoItem? FindPhoto(string? id) => id == null ? null : Library.FirstOrDefault(p => p.Id == id);
}

/// <summary>
/// 상태 파일 저장소: %AppData%\GifPin\state.json
/// 변경은 UI 스레드에서 SaveSoon()으로 알리고, 실제 디스크 쓰기는 1초 뒤 한 번에 한다.
/// </summary>
public static class Store
{
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GifPin");
    public static string LibraryDir => Path.Combine(DataDir, "Library");
    public static string ThumbDir => Path.Combine(DataDir, "Thumbs");
    private static string StateFile => Path.Combine(DataDir, "state.json");
    private static string BackupFile => Path.Combine(DataDir, "state.bak");
    private static string PrototypeFile => Path.Combine(DataDir, "prototype.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly object Gate = new();
    private static string? _pending;
    private static Timer? _timer;

    public static string ResolvePath(PhotoItem p) =>
        Path.IsPathRooted(p.File) ? p.File : Path.Combine(DataDir, p.File);

    public static string ThumbPath(PhotoItem p) => Path.Combine(ThumbDir, p.Id + ".png");

    public static AppState Load()
    {
        Directory.CreateDirectory(DataDir);
        foreach (var file in new[] { StateFile, BackupFile })
        {
            try
            {
                if (System.IO.File.Exists(file))
                {
                    var s = JsonSerializer.Deserialize<AppState>(System.IO.File.ReadAllText(file), JsonOptions);
                    if (s != null) return Migrate(s);
                }
            }
            catch { /* 손상 → 다음 후보(백업) */ }
        }
        return Migrate(MigrateFromPrototype() ?? new AppState { Settings = { OpenWindowOnStart = true } });
    }

    /// <summary>v0.1 프로토타입 설정(prototype.json)이 있으면 그 GIF를 라이브러리와 화면에 옮긴다.</summary>
    /// <summary>예전 버전 설정 옮기기.</summary>
    internal static AppState Migrate(AppState s)
    {
        s.Settings ??= new AppSettings();
        s.Library ??= new();
        s.Active ??= new();
        s.Scenes ??= new();
        s.EffectPresets ??= new();
        s.EffectPresets.RemoveAll(x => x == null || x.Values == null);
        foreach (var ep in s.EffectPresets) { ep.Values.Normalize(); if (string.IsNullOrEmpty(ep.Id)) ep.Id = NewId("e"); }
        s.Scenes.RemoveAll(sc => sc == null);
        foreach (var sc in s.Scenes)
        {
            sc.Items ??= new();
            sc.Items.RemoveAll(i => i == null);
            sc.AutoAt = Scene.NormalizeTime(sc.AutoAt);
            if (string.IsNullOrEmpty(sc.Id)) sc.Id = NewId("s");
        }
        s.Settings.Normalize();
        if (s.Settings.SettingsVersion < 2)
        {
            // v0.4.4: 전체 화면에서도 효과를 켜 두는 것이 기본으로 바뀜
            s.Settings.PauseEffectsFullscreen = false;
            s.Settings.SettingsVersion = 2;
        }
        if (s.Settings.SettingsVersion < 3)
        {
            // v0.6.0: 성능 모드 — 예전 설정이 "균형"과 같으면 균형, 아니면 직접 설정
            s.Settings.PerfMode = PerfModes.Detect(s.Settings);
            s.Settings.SettingsVersion = 3;
        }
        return s;
    }

    private static AppState? MigrateFromPrototype()
    {
        try
        {
            if (!System.IO.File.Exists(PrototypeFile)) return null;
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(PrototypeFile));
            var root = doc.RootElement;
            var state = new AppState { Settings = { OpenWindowOnStart = true } };
            if (root.TryGetProperty("GifPath", out var gp) && gp.GetString() is string path && System.IO.File.Exists(path))
            {
                var added = Library.AddFiles(new[] { path }, state, copy: true);
                var photo = added.Added.FirstOrDefault();
                if (photo != null)
                {
                    var active = new ActiveItem { Id = NewId("a"), PhotoId = photo.Id };
                    if (root.TryGetProperty("X", out var x) && x.ValueKind == JsonValueKind.Number) active.X = x.GetInt32();
                    if (root.TryGetProperty("Y", out var y) && y.ValueKind == JsonValueKind.Number) active.Y = y.GetInt32();
                    if (root.TryGetProperty("Scale", out var sc) && sc.ValueKind == JsonValueKind.Number) active.Scale = sc.GetDouble();
                    state.Active.Add(active);
                    photo.LastUsedAt = DateTimeOffset.Now;
                }
            }
            SaveNow(state);
            return state;
        }
        catch { return null; }
    }

    public static string NewId(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];

    /// <summary>UI 스레드에서 호출: 지금 상태를 직렬화해 두고 1초 뒤 디스크에 쓴다.</summary>
    /// <summary>
    /// 1초 동안 변경이 없으면 저장. 변경이 계속 이어져도 첫 변경 후 5초 안에는 반드시 저장
    /// (계속 미뤄지다가 비정상 종료 때 잃어버리지 않게).
    /// </summary>
    public static void SaveSoon(AppState state)
    {
        string json = JsonSerializer.Serialize(state, JsonOptions);
        lock (Gate)
        {
            long now = Environment.TickCount64;
            if (_pending == null) _firstPendingAt = now;
            _pending = json;
            _timer ??= new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
            long wait = Math.Clamp(5000 - (now - _firstPendingAt), 0, 1000);
            _timer.Change(wait, Timeout.Infinite);
        }
    }

    private static long _firstPendingAt;
    private static readonly object WriteGate = new();

    public static void SaveNow(AppState state)
    {
        lock (Gate) _pending = JsonSerializer.Serialize(state, JsonOptions);
        Flush();
    }

    private static void Flush()
    {
        string? json;
        lock (Gate)
        {
            json = _pending;
            _pending = null;
        }
        if (json == null) return;
        lock (WriteGate) // 타이머 저장과 종료 저장이 겹쳐도 파일을 한 번에 하나씩
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                var tmp = StateFile + ".tmp";
                System.IO.File.WriteAllText(tmp, json);
                if (System.IO.File.Exists(StateFile)) System.IO.File.Copy(StateFile, BackupFile, overwrite: true);
                System.IO.File.Move(tmp, StateFile, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Warn("Saving settings failed; will retry", ex);
                lock (Gate) _pending ??= json; // 더 새 내용이 없으면 다시 시도할 수 있게 돌려놓음
            }
        }
    }
}

/// <summary>GIF를 그리는 층.</summary>
public static class Layers
{
    public const string Top = "top";         // 모든 프로그램 위 (게임 포함)
    public const string Desktop = "desktop"; // 바탕화면 바로 위, 모든 프로그램 아래
    public static string Normalize(string? layer) => layer == Desktop ? Desktop : Top;
}

/// <summary>성능 모드: 여러 성능 설정을 한 번에 정하는 묶음.</summary>
public static class PerfModes
{
    public const string Saver = "saver", Balanced = "balanced", Quality = "quality", Custom = "custom";

    public static string Normalize(string? mode) => mode is Saver or Balanced or Quality or Custom ? mode : Balanced;

    /// <summary>모드의 값을 설정에 적용 (직접 설정이면 그대로).</summary>
    public static void Apply(AppSettings s, string mode)
    {
        s.PerfMode = Normalize(mode);
        switch (s.PerfMode)
        {
            case Saver:
                s.EffectsFps = 30; s.MaxParticlesTotal = 150; s.GifFpsCap = 20; s.RenderQuality = "low"; s.MemoryBudgetMB = 192;
                s.AutoLightEffects = true; s.AutoLightCpuPercent = 70; s.PauseEffectsFullscreen = true; s.PauseEffectsOnBattery = true;
                break;
            case Balanced:
                s.EffectsFps = 60; s.MaxParticlesTotal = 400; s.GifFpsCap = 0; s.RenderQuality = "medium"; s.MemoryBudgetMB = 384;
                s.AutoLightEffects = true; s.AutoLightCpuPercent = 80; s.PauseEffectsFullscreen = false; s.PauseEffectsOnBattery = false;
                break;
            case Quality:
                s.EffectsFps = 60; s.MaxParticlesTotal = 1000; s.GifFpsCap = 0; s.RenderQuality = "high"; s.MemoryBudgetMB = 768;
                s.AutoLightEffects = false; s.AutoLightCpuPercent = 90; s.PauseEffectsFullscreen = false; s.PauseEffectsOnBattery = false;
                break;
        }
    }

    /// <summary>지금 값이 어느 모드와 똑같은지 (아니면 직접 설정).</summary>
    public static string Detect(AppSettings s)
    {
        foreach (var mode in new[] { Balanced, Saver, Quality })
        {
            var probe = new AppSettings();
            Apply(probe, mode);
            if (probe.EffectsFps == s.EffectsFps && probe.MaxParticlesTotal == s.MaxParticlesTotal && probe.GifFpsCap == s.GifFpsCap
                && probe.RenderQuality == s.RenderQuality && probe.MemoryBudgetMB == s.MemoryBudgetMB && probe.AutoLightEffects == s.AutoLightEffects
                && (!probe.AutoLightEffects || probe.AutoLightCpuPercent == s.AutoLightCpuPercent)
                && probe.PauseEffectsFullscreen == s.PauseEffectsFullscreen && probe.PauseEffectsOnBattery == s.PauseEffectsOnBattery)
                return mode;
        }
        return Custom;
    }
}
