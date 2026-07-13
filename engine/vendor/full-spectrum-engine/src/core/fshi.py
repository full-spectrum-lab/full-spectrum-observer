#!/usr/bin/env python3
"""
全频谱协议 · FSHI 频段健康指数
FSHI = w_l·S_l + w_m·S_m + w_h·S_h
"""

from dataclasses import dataclass
from typing import Tuple
from .state import CivilizationState


@dataclass
class FSHIConfig:
    """FSHI 权重配置"""
    w_l: float = 0.40  # 生存权重
    w_m: float = 0.35  # 协调权重
    w_h: float = 0.25  # 意义权重
    
    def validate(self) -> bool:
        """验证权重和为 1"""
        total = self.w_l + self.w_m + self.w_h
        if abs(total - 1.0) < 1e-6:
            return True
        raise ValueError(f"权重和必须为1，当前为 {total}")
    
    def to_tuple(self) -> Tuple[float, float, float]:
        return (self.w_l, self.w_m, self.w_h)


def compute_fshi(state: CivilizationState, config: FSHIConfig, penalty: float = 0.0) -> float:
    """
    计算频段健康指数
    FSHI = w_l·S_l + w_m·S_m + w_h·S_h - Penalty
    """
    config.validate()
    raw = config.w_l * state.survival + config.w_m * state.coordination + config.w_h * state.meaning
    return max(0.0, min(100.0, raw * 100 - penalty))


def fshi_risk_level(fshi: float) -> str:
    """FSHI 风险分级"""
    if fshi >= 80:
        return "EXCELLENT"
    elif fshi >= 60:
        return "NORMAL"
    elif fshi >= 40:
        return "WARNING"
    elif fshi >= 20:
        return "CRISIS"
    else:
        return "CRITICAL"
