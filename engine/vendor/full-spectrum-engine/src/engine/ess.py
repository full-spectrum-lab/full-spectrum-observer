#!/usr/bin/env python3
"""
全频谱协议 · ESS 伦理觉性模拟器
在决策前模拟多条路径的 3 阶后果，选择痛苦最小的路径
"""

from dataclasses import dataclass, field
from typing import List, Optional, Tuple
import numpy as np

from ..core.state import CivilizationState, project_to_feasible


@dataclass
class ESSConfig:
    horizon: int = 5              # 推演步数
    num_candidates: int = 10      # 候选策略数量
    pain_penalty: float = 1.0     # 越界惩罚


@dataclass
class ESSPath:
    """单条推演路径"""
    option: str
    selected: bool
    low_freq_impact: float
    mid_freq_impact: float
    high_freq_impact: float
    total_pain: float
    description: str = ""


@dataclass
class ESSResult:
    """ESS 推演结果"""
    paths: List[ESSPath]
    horizon: int
    selected_option: str


class ESS:
    """伦理觉性模拟器"""
    
    def __init__(self, config: Optional[ESSConfig] = None, horizon: Optional[int] = None):
        self.config = config or ESSConfig()
        if horizon is not None:
            self.config.horizon = horizon
        self._history: List[ESSResult] = []
    
    def pain(self, S: CivilizationState) -> float:
        """
        痛苦指数计算
        Pain = (1-S_l) + (1-S_m) + (1-S_h) + Penalty
        """
        base = (1 - S.survival) + (1 - S.coordination) + (1 - S.meaning)
        penalty = 0.0
        if S.survival < 0.3:
            penalty += self.config.pain_penalty
        if S.coordination > 0.8:
            penalty += self.config.pain_penalty
        if S.meaning < 0.2:
            penalty += self.config.pain_penalty
        return base + penalty
    
    def simulate(self, S: CivilizationState, W: np.ndarray) -> float:
        """
        模拟单条策略路径，返回总痛苦
        """
        S_curr = S
        total_pain = 0.0
        for _ in range(self.config.horizon):
            # 简化的动力学步进
            noise = np.random.normal(0, 0.02, 3)
            dS = -0.02 * S_curr.to_array() + 0.1 * W + 0.02 * noise
            S_curr = CivilizationState.from_array(S_curr.to_array() + dS * 0.1)
            S_curr = project_to_feasible(S_curr)
            total_pain += self.pain(S_curr)
        return total_pain
    
    def generate_candidates(self, S: CivilizationState) -> List[np.ndarray]:
        """生成候选策略"""
        base = np.array([0.4, 0.3, 0.3])
        candidates = [base]
        for _ in range(self.config.num_candidates - 1):
            noise = np.random.normal(0, 0.05, 3)
            candidate = np.clip(base + noise, 0, 1)
            candidates.append(candidate)
        return candidates
    
    def select_strategy_with_result(self, S: CivilizationState) -> Tuple[np.ndarray, ESSResult]:
        """选择最优策略"""
        candidates = self.generate_candidates(S)
        results = []
        for W in candidates:
            total_pain = self.simulate(S, W)
            results.append((W, total_pain))
        
        results.sort(key=lambda x: x[1])
        best_W = results[0][0]
        best_pain = results[0][1]
        
        # 构建路径结果
        paths = []
        for i, (W, pain) in enumerate(results[:5]):
            paths.append(ESSPath(
                option=f"PATH-{i+1:03d}",
                selected=(i == 0),
                low_freq_impact=float(W[0]),
                mid_freq_impact=float(W[1]),
                high_freq_impact=float(W[2]),
                total_pain=float(pain),
                description=f"总痛苦={pain:.3f}"
            ))
        
        result = ESSResult(
            paths=paths,
            horizon=self.config.horizon,
            selected_option=paths[0].option if paths else "NONE"
        )
        self._history.append(result)
        
        return best_W, result

    def select_strategy(self, S: CivilizationState) -> np.ndarray:
        """选择最优策略向量。

        兼容实验脚本中 `W = ess.select_strategy(S)` 的用法；完整 ESSResult
        会保存在 `_history` 中，如需同时获取结果可调用
        `select_strategy_with_result()`。
        """
        best_W, _ = self.select_strategy_with_result(S)
        return best_W
