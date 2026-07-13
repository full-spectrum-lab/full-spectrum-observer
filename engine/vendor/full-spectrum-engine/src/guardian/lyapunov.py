#!/usr/bin/env python3
"""
全频谱协议 · Lyapunov稳定性调节器
监控系统状态是否在吸引域内，并在偏离时施加控制
"""

from dataclasses import dataclass
from typing import Tuple, Optional, List, Dict
import numpy as np

from ..core.state import CivilizationState


@dataclass
class LyapunovConfig:
    """Lyapunov调节器配置"""
    epsilon1: float = 0.01   # 吸引域内阈值
    epsilon2: float = 0.05   # 偏离预警阈值
    gamma: float = 0.1       # 控制增益


class LyapunovRegulator:
    """Lyapunov稳定性调节器"""
    
    def __init__(
        self,
        target: CivilizationState,
        config: Optional[LyapunovConfig] = None,
        weights: Tuple[float, float, float] = (1.0, 1.0, 1.0)
    ):
        self.target = target
        self.config = config or LyapunovConfig()
        self.weights = np.array(weights)
        self._history: List[Dict] = []
    
    def compute_v(self, S: CivilizationState) -> float:
        """
        计算Lyapunov函数值
        V(S) = ½(S - S*)ᵀ P (S - S*)
        """
        delta = S.to_array() - self.target.to_array()
        weighted = self.weights * delta
        return 0.5 * np.dot(weighted, delta)
    
    def compute_dvdt(self, S: CivilizationState, dS: CivilizationState) -> float:
        """
        计算Lyapunov函数变化率
        dV/dt = (S - S*)ᵀ P Ṡ
        """
        delta = S.to_array() - self.target.to_array()
        dS_vec = dS.to_array()
        weighted = self.weights * delta
        return np.dot(weighted, dS_vec)
    
    def check_stability(
        self,
        S: CivilizationState,
        dS: CivilizationState
    ) -> Tuple[bool, str, Optional[np.ndarray]]:
        """
        检查稳定性并返回控制信号
        
        Returns:
            (stable, status, control_signal)
        """
        V = self.compute_v(S)
        dVdt = self.compute_dvdt(S, dS)
        
        self._history.append({
            "timestamp": time.time(),
            "V": V,
            "dVdt": dVdt,
            "state": S
        })
        
        # 判据1：超出吸引域
        if V >= self.config.epsilon2:
            return False, f"OUT_OF_ATTRACTOR: V={V:.4f}", self._compute_control(S, V)
        
        # 判据2：发散趋势
        if len(self._history) > 10:
            recent = [h["dVdt"] for h in self._history[-10:]]
            if all(dv > 0 for dv in recent):
                return False, f"DIVERGENT_TREND: dVdt持续为正", self._compute_control(S, V)
        
        # 判据3：预警
        if V >= self.config.epsilon1:
            return True, f"WARNING: V={V:.4f}", self._compute_control(S, V, weak=True)
        
        return True, f"STABLE: V={V:.4f}", None
    
    def _compute_control(self, S: CivilizationState, V: float, weak: bool = False) -> np.ndarray:
        """
        计算控制信号
        控制信号 = -γ · ∇V · V
        """
        delta = S.to_array() - self.target.to_array()
        gradient = self.weights * delta
        
        gain = self.config.gamma * 0.5 if weak else self.config.gamma
        control = -gain * gradient * V
        
        return control
    
    def apply_control(self, S: CivilizationState, control: np.ndarray) -> CivilizationState:
        """应用控制信号到状态"""
        S_new = S.to_array() + control
        return CivilizationState.from_array(S_new)
    
    def get_history(self) -> List[Dict]:
        return self._history.copy()
    
    def get_convergence_rate(self) -> Optional[float]:
        """计算收敛速率"""
        if len(self._history) < 2:
            return None
        
        V_values = [h["V"] for h in self._history]
        if V_values[-1] == 0:
            return None
        
        return V_values[-1] / V_values[0]
