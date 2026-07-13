#!/usr/bin/env python3
"""
Full Spectrum Engine — 仿真入口脚本

用法:
    python simulate.py --config examples/scenario_refund_conflict.json
    python simulate.py --config examples/scenario_refund_conflict.json --output result.json

输入: scenario.json (场景配置)
输出: 结构化 JSON 结果 (FSHI、风险向量、符石、因果链)
"""

import argparse
import hashlib
import json
import sys
import os
import time
from typing import Optional

import numpy as np

# 确保能导入 src 包
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from src.core.state import CivilizationState, project_to_feasible
from src.core.fshi import compute_fshi, FSHIConfig, fshi_risk_level
from src.engine.ess import ESS, ESSConfig
from src.engine.agents import create_default_agents, aggregate_strategies, AuthorityMatrix
from src.governance.validator import DreamButterflyValidator
from src.governance.bomb import AwarenessBombEngine
from src.bridge.runestone import Runestone, RiskVector, ReasonField
from src.safety.emergency import EmergencyBrakeEngine
from src.report_generator import ReportGenerator


DETERMINISTIC_TIMESTAMP = "2026-07-04T00:00:00Z"
DETERMINISTIC_UNIX_TS = 1783123200.0


def load_scenario(config_path: str) -> dict:
    """加载场景配置文件"""
    with open(config_path, "r", encoding="utf-8") as f:
        return json.load(f)


def _stable_id(prefix: str, payload: dict, length: int = 16) -> str:
    """基于结构化 payload 生成稳定 ID，用于 golden sample。"""
    encoded = json.dumps(payload, ensure_ascii=False, sort_keys=True).encode("utf-8")
    return f"{prefix}_{hashlib.sha256(encoded).hexdigest()[:length]}"


def run_simulation(scenario: dict, seed: Optional[int] = None, fixed_time: Optional[str] = None) -> dict:
    """执行完整仿真流程"""
    if seed is not None:
        np.random.seed(seed)

    sim_id = scenario.get("simulation_id", f"SIM-{int(time.time())}")
    initial = scenario.get("initial_state", {"survival": 0.7, "coordination": 0.6, "meaning": 0.5})
    agents_config = scenario.get("agents", [])
    input_query = scenario.get("input_query", "")
    sensitivity = scenario.get("sensitivity_level", "medium")
    enterprise_id = scenario.get("enterprise_id", "default")
    rule_version = scenario.get("rule_version", "v0.3")
    output_timestamp = (
        fixed_time
        or scenario.get("timestamp")
        or (DETERMINISTIC_TIMESTAMP if seed is not None else time.strftime("%Y-%m-%dT%H:%M:%SZ"))
    )
    runestone_timestamp = scenario.get(
        "timestamp_unix",
        (DETERMINISTIC_UNIX_TS + float(seed) if seed is not None else time.time())
    )

    # 1. 初始化文明状态
    S = CivilizationState(
        survival=initial.get("survival", 0.7),
        coordination=initial.get("coordination", 0.6),
        meaning=initial.get("meaning", 0.5),
    )

    # 2. 运行 ESS 伦理觉性模拟器
    ess_config = ESSConfig(
        horizon=scenario.get("ess_horizon", 5),
        num_candidates=scenario.get("ess_candidates", 10),
    )
    ess = ESS(ess_config)
    best_W, ess_result = ess.select_strategy_with_result(S)

    # 3. 多 Agent 策略聚合
    agents = create_default_agents()
    authority = AuthorityMatrix()
    aggregated = aggregate_strategies(agents, S, authority)

    # 4. 应用策略到状态
    S_new = CivilizationState(
        survival=max(0.0, min(1.0, S.survival + float(aggregated[0]) * 0.05)),
        coordination=max(0.0, min(1.0, S.coordination + float(aggregated[1]) * 0.05)),
        meaning=max(0.0, min(1.0, S.meaning + float(aggregated[2]) * 0.05)),
    )

    # 5. 计算 FSHI（P0-1 修复：支持 adapter penalty）
    fshi_config = FSHIConfig(
        w_l=scenario.get("weights", {}).get("survival", 0.40),
        w_m=scenario.get("weights", {}).get("coordination", 0.35),
        w_h=scenario.get("weights", {}).get("meaning", 0.25),
    )
    fshi_penalty = scenario.get("fshi_penalty", 0.0)
    fshi = compute_fshi(S_new, fshi_config, penalty=fshi_penalty)
    risk_level = fshi_risk_level(fshi)

    # 6. 梦蝶校验
    validator = DreamButterflyValidator()
    passed, reason, S_corrected = validator.validate(
        S_new,
        predictions=[best_W],
        strategies=[aggregated],
    )
    if S_corrected is not None:
        S_new = S_corrected

    # 7. 觉性炸弹检查
    bomb = AwarenessBombEngine()
    bomb_triggered, bomb_reason = bomb.check_trigger(
        S_new,
        purity=0.75,  # 简化：实际应从 L4 获取
        rigidity=0.3,
    )
    if bomb_triggered:
        S_new = bomb.detonate(S_new, bomb_reason)

    # 8. 紧急制动监控
    brake = EmergencyBrakeEngine()
    brake_state, brake_event = brake.monitor(
        S_new,
        fshi=fshi,
        conflict_density=scenario.get("conflict_density", 0.0),
        pain_score=ess_result.paths[0].total_pain / 10.0 if ess_result.paths else 0.0,
        bomb_triggered=bomb_triggered,
    )

    # 9. 构建风险向量
    # P0-2 修复：优先读取 irreversibility，向后兼容 reversibility
    irreversibility_val = scenario.get("irreversibility", scenario.get("reversibility", 0.5))
    risk_vector = RiskVector(
        survival_impact=1.0 - S_new.survival,
        trust_impact=1.0 - S_new.coordination,
        meaning_impact=1.0 - S_new.meaning,
        reversibility=irreversibility_val,  # RiskVector 字段名保留（协议兼容），语义为不可逆风险强度
        explainability=0.8,
        diffusivity=scenario.get("diffusivity", 0.3),
        urgency=0.1 if risk_level in ("EXCELLENT", "NORMAL") else 0.5,
        uncertainty=0.2,
    )

    # 10. 生成符石
    reason_field = ReasonField(enterprise_id=enterprise_id, rule_version=rule_version)
    runestone_id = None
    causal_chain_id = None
    if seed is not None:
        deterministic_payload = {
            "seed": seed,
            "simulation_id": sim_id,
            "selected_option": ess_result.selected_option,
            "reason": str(reason_field),
            "risk_vector": risk_vector.to_dict(),
            "ess": {
                "horizon": ess_result.horizon,
                "num_paths": len(ess_result.paths),
                "selected_pain": ess_result.paths[0].total_pain if ess_result.paths else 0.0,
            },
        }
        runestone_id = _stable_id("RS", deterministic_payload)
        causal_chain_id = _stable_id("CC", {**deterministic_payload, "timestamp": output_timestamp})

    runestone = Runestone.create(
        decision=ess_result.selected_option,
        reason=str(reason_field),
        risk_vector=risk_vector,
        agents=[a.get_name() for a in agents],
        ess_data={
            "horizon": ess_result.horizon,
            "num_paths": len(ess_result.paths),
            "selected_pain": ess_result.paths[0].total_pain if ess_result.paths else 0.0,
        },
        runestone_id=runestone_id,
        timestamp=runestone_timestamp,
    )

    # 11. 生成因果链报告
    report_gen = ReportGenerator()
    report = report_gen.generate(
        ess_result=ess_result,
        dream_butterfly_result={
            "passed": passed,
            "reason": reason,
            "bomb_triggered": bomb_triggered,
            "bomb_reason": bomb_reason,
        },
        state=S_new,
        runestone=runestone,
        selected_option=ess_result.selected_option,
        judgment_basis=[
            f"FSHI={fshi:.1f} ({risk_level})",
            f"ESS selected {ess_result.selected_option} (pain={ess_result.paths[0].total_pain:.3f})" if ess_result.paths else "No paths",
            f"Bomb: {'TRIGGERED - ' + bomb_reason if bomb_triggered else 'not triggered'}",
        ],
        system_state=risk_level,
        spectrum_priority="survival" if S_new.survival < 0.4 else ("coordination" if S_new.coordination > 0.7 else "meaning"),
        timestamp=output_timestamp,
        causal_chain_id=causal_chain_id,
    )

    # 12. 组装输出
    output = {
        "simulation_id": sim_id,
        "timestamp": output_timestamp,
        "input_query": input_query,
        "sensitivity_level": sensitivity,
        "initial_state": {"survival": S.survival, "coordination": S.coordination, "meaning": S.meaning},
        "final_state": {"survival": S_new.survival, "coordination": S_new.coordination, "meaning": S_new.meaning},
        "fshi": {
            "value": round(fshi, 2),
            "risk_level": risk_level,
            "weights": {"survival": fshi_config.w_l, "coordination": fshi_config.w_m, "meaning": fshi_config.w_h},
            "penalty": round(fshi_penalty, 4),
        },
        "ess": {
            "selected_option": ess_result.selected_option,
            "horizon": ess_result.horizon,
            "paths": [
                {
                    "option": p.option,
                    "selected": p.selected,
                    "total_pain": round(p.total_pain, 4),
                    "low_freq": round(p.low_freq_impact, 4),
                    "mid_freq": round(p.mid_freq_impact, 4),
                    "high_freq": round(p.high_freq_impact, 4),
                }
                for p in ess_result.paths
            ],
        },
        "validation": {
            "dream_butterfly_passed": passed,
            "dream_butterfly_reason": reason,
            "bomb_triggered": bomb_triggered,
            "bomb_reason": bomb_reason,
            "brake_state": brake_state.value if brake_state else "NORMAL",
        },
        "risk_vector": risk_vector.to_dict(),
        "runestone": runestone.to_dict(),
        "causal_chain": report,
    }

    return output


def main():
    parser = argparse.ArgumentParser(
        description="Full Spectrum Engine — 仿真入口",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
示例:
  python simulate.py --config examples/scenario_refund_conflict.json
  python simulate.py --config examples/scenario_refund_conflict.json --output result.json
  python simulate.py --config examples/scenario_refund_conflict.json --seed 42
        """,
    )
    parser.add_argument(
        "--config", "-c",
        required=True,
        help="场景配置文件路径 (JSON)",
    )
    parser.add_argument(
        "--output", "-o",
        default=None,
        help="输出文件路径 (不指定则打印到 stdout)",
    )
    parser.add_argument(
        "--seed",
        type=int,
        default=None,
        help="固定随机种子，用于生成可复现 golden sample",
    )
    parser.add_argument(
        "--fixed-time",
        default=None,
        help="固定输出时间戳，例如 2026-07-04T00:00:00Z；默认在 --seed 时使用稳定时间",
    )
    args = parser.parse_args()

    # 加载场景
    try:
        scenario = load_scenario(args.config)
    except FileNotFoundError:
        print(f"Error: config file not found: {args.config}", file=sys.stderr)
        sys.exit(1)
    except json.JSONDecodeError as e:
        print(f"Error: failed to parse JSON: {e}", file=sys.stderr)
        sys.exit(1)

    # 运行仿真
    result = run_simulation(scenario, seed=args.seed, fixed_time=args.fixed_time)

    # 输出结果
    output_json = json.dumps(result, ensure_ascii=False, indent=2)

    if args.output:
        with open(args.output, "w", encoding="utf-8") as f:
            f.write(output_json)
        print(f"Output written to: {args.output}", file=sys.stderr)
    else:
        print(output_json)


if __name__ == "__main__":
    main()
