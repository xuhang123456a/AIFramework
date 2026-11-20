#nullable enable
using System;
using AIFramework.Contracts.Input;
using AIFramework.Adapters.Unity.Battle;
using UnityEngine;

namespace AIFramework.Adapters.Unity.Inputs
{
    /// <summary>
    /// 场景输入路由（抽象基类）：每帧从 <see cref="IInputSource"/> 拉一份 <see cref="InputFrame"/>，
    /// 把动作转发给子类绑定的控制器。
    /// <para>
    /// <b>为什么单独一个物体/组件，而不是塞进 RunnerController / PlatformerController</b>：
    /// 1) 控制器已经承担"装配宿主 + 驱动内核 + 渲染 HUD"三件事，再让它认识键鼠/触屏就把设备差异
    ///    焊进了玩法适配层；2) 同一份 <see cref="IInputSource"/> 将来要接 Console 回放、AI 脚本、
    ///    手柄，控制器不该为每种来源改一次；3) 场景里"谁响应输入"变成一条 Inspector 上的显式引用，
    ///    比 <c>FindObjectOfType</c> 可靠（后者在场景重载/多场景叠加时会拿到别的实例）。
    /// </para>
    /// <para>
    /// <b>为什么是"抽象基类 + 两个场景专属子类"，而不是"一个类挂两个可空控制器引用"</b>
    /// （这是对需求里"一个类 + 两个字段"设计的<b>唯一偏离</b>，原因写在这里，便于一次看懂）：
    /// 场景生成流程末尾会调 <c>SceneVerifier.VerifyAll()</c>，它把<b>所有</b>非值类型、非字符串、
    /// 非数组的序列化引用字段都判成"漏注入"（见 <c>SceneVerifier.VerifySerializedReferences</c>），
    /// 而 RunnerScene 里不存在 <see cref="PlatformerController"/>、PlatformerScene 里不存在
    /// <see cref="RunnerController"/>——两个字段必然有一个是 null，场景生成会在校验阶段直接中止。
    /// 校验器是"漏注入"的核心保护，不在本轮可改范围；因此用"每个子类只声明一个<b>必定被注入</b>的
    /// 按类型引用字段"来同时满足：按类型注入、Inspector 可读、场景校验全绿、不碰 SceneVerifier。
    /// 若将来允许给 SceneVerifier 加"可选引用"白名单，可以合并回单个类，两个字段的语义完全一致。
    /// </para>
    /// <para>
    /// <b>为什么命名空间是 <c>Inputs</c> 而不是 <c>Input</c></b>：与 <c>Runtime/UI</c> 目录用
    /// <c>AIFramework.Adapters.Unity.Ui</c> 命名空间避开 <c>UnityEngine.UI</c> 是同一原因——
    /// 若命名空间叫 <c>...Unity.Input</c>，则在本命名空间内裸写 <c>Input.GetKeyDown</c> 会先解析到
    /// 这个子命名空间（封闭命名空间成员优先于 <c>using UnityEngine;</c>），迫使所有调用点全限定成
    /// <c>UnityEngine.Input</c>。目录名保持 <c>Runtime/Input/</c> 只影响文件组织。
    /// </para>
    /// <para>
    /// <b>与控制器 <c>autoPlay</c> 的关系（本轮锁定行为）</b>：场景默认 <c>autoPlay = true</c>（演示需求），
    /// 本路由<b>不</b>做 <c>if (autoPlay) return;</c>，按键照常转发。理由：
    /// 1) 两个控制器都没有公开 autoPlay 属性，本轮又不允许改控制器，路由无从读取该状态；
    /// 2) 若在演示期间静默丢键，玩家看到的现象是"键盘坏了"，而不是"演示没停"；
    /// 3) 转发本身无害——跳跃/下蹲是否生效由内核裁决（空中会被拒），平台跳跃的方向输入会被自动演示
    /// 每帧覆写，因此"按键打断演示"只在 Inspector 关掉 AutoPlay 后才是稳定行为（Update 执行顺序
    /// 未在 Project Settings 里固定）。下一轮若要让按键在演示中真正抢占，正确做法是给控制器加公开的
    /// <c>autoPlay</c> 开关并在这里置 false，而不是在路由里复制一份演示状态。
    /// </para>
    /// <para>
    /// <b>测试注入</b>：<see cref="SetSourceForTests"/> 允许 PlayMode 用例把真实设备源替换成
    /// <c>ScriptedInputSource</c>（纯逻辑，不依赖设备），从而在 CI 里断言"按键 → 控制器方法"这条链路；
    /// <see cref="RoutedCount"/> 是这条链路的计数证据（控制器没有公开的输入计数）。
    /// </para>
    /// </summary>
    public abstract class SceneInputRouter : MonoBehaviour
    {
        /// <summary>是否组装键盘/鼠标源（默认开；Inspector 可单独关掉排查设备问题）。</summary>
        [SerializeField] private bool enableKeyboard = true;

        /// <summary>是否组装触屏源（默认开；Editor 里 touchCount 恒 0，等于空转）。</summary>
        [SerializeField] private bool enableTouch = true;

        /// <summary>当前输入源（<see cref="Awake"/> 组装，测试可替换）。</summary>
        private IInputSource? _source;

        /// <summary>已成功转发给控制器的<u>动作边沿</u>次数（Jump/Slide；轴路由不计）。</summary>
        private int _routedCount;

        /// <summary>
        /// 已成功转发给控制器的动作边沿次数（生命周期累计）。
        /// <para>测试证据：控制器没有公开的输入计数，这里记"路由确实把动作交出去了"。
        /// 只统计 Jump/Slide 这类边沿动作，不含横向轴（否则按住一个方向每帧都会 +1，无法作为断言依据）。</para>
        /// </summary>
        public int RoutedCount
        {
            get { return _routedCount; }
        }

        /// <summary>
        /// 替换输入源（PlayMode 测试专用）：把真实设备源换成 <c>ScriptedInputSource</c>，
        /// 让"按键 → 控制器方法"链路能在无设备环境下被驱动与断言。
        /// <para>替换后原源被丢弃（下帧起只 Poll 新源）；传 null 视为装配错误，直接抛出。</para>
        /// </summary>
        /// <param name="source">新的输入源。</param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> 为 null。</exception>
        public void SetSourceForTests(IInputSource source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source), "输入源不能为 null；没有输入时应该禁用本组件。");
            }

            _source = source;
        }

        /// <summary>
        /// 组装输入源：按开关拼键盘源与触屏源；两者都开时用 <see cref="CompositeInputSource"/> 合并，
        /// 都关时不建源（<see cref="Update"/> 直接空转，不做无意义的 Poll）。
        /// </summary>
        protected virtual void Awake()
        {
            _source = ComposeSources();
        }

        /// <summary>
        /// 每帧：拉一次输入 → 按动作转发给子类。
        /// <para><b>顺序</b>：先 Poll（消费本帧边沿），再逐动作转发；同一帧多个动作互不影响。</para>
        /// <para><b>为什么只处理 Jump/Slide/横向轴</b>：这两个演示场景的控制器只有 Jump / Slide / Move
        /// 这类公开面；<see cref="InputAction.Primary"/> / <see cref="InputAction.Secondary"/> /
        /// <see cref="InputAction.Pause"/> 仍会出现在 <see cref="InputFrame"/> 里，但本场景没有语义可接
        /// （不臆造"右键 = 重开"这种映射）。</para>
        /// </summary>
        protected virtual void Update()
        {
            IInputSource? source = _source;
            if (source == null)
            {
                return;
            }

            InputFrame frame = source.Poll();

            if (frame.IsPressed(InputAction.Jump))
            {
                RouteJump();
            }

            if (frame.IsPressed(InputAction.Slide))
            {
                RouteSlide();
            }

            RouteHorizontal(frame.Horizontal);
        }

        /// <summary>本组件销毁：丢弃源引用，避免场景重载后残留（源是纯 C# 对象，无原生资源要释放）。</summary>
        protected virtual void OnDestroy()
        {
            _source = null;
        }

        /// <summary>
        /// 按下"跳"时调用：由子类把动作交给自己的控制器。
        /// <para><b>为什么不做"能不能跳"的前置判断</b>：空中 <c>TryJump</c> 返回 <c>false</c> 是内核规则，
        /// 路由再判一遍就会在内核改规则时静默分叉。</para>
        /// </summary>
        protected abstract void RouteJump();

        /// <summary>按下"下蹲"时调用：没有下蹲能力的场景应显式空实现并注释原因（而不是删掉抽象方法）。</summary>
        protected abstract void RouteSlide();

        /// <summary>本帧横向轴（-1..1）到达时调用；默认空实现（例如跑酷是自动前进，横向轴无意义）。</summary>
        /// <param name="horizontal">本帧横向轴（已由契约归一化）。</param>
        protected virtual void RouteHorizontal(float horizontal)
        {
        }

        /// <summary>记一次"动作已成功交给控制器"（<see cref="RoutedCount"/> 的唯一写入点）。</summary>
        protected void CountRouted()
        {
            _routedCount++;
        }

        /// <summary>按开关组装源（两者都关时返回 null）。</summary>
        /// <returns>组装好的输入源，或 null。</returns>
        private IInputSource? ComposeSources()
        {
            if (enableKeyboard && enableTouch)
            {
                return new CompositeInputSource(new KeyboardInputSource(), new TouchInputSource());
            }

            if (enableKeyboard)
            {
                return new KeyboardInputSource();
            }

            if (enableTouch)
            {
                return new TouchInputSource();
            }

            return null;
        }
    }
}
