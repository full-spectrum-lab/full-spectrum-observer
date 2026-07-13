#!/usr/bin/env python3
"""
全频谱协议 · 多Agent系统
AI/人类/监管/企业 四类治理主体的策略聚合
"""

from abc import ABC, abstractmethod
from typing import List, Dict, Any, Optional
import time
import numpy as np

from ..core.state import CivilizationState


class Agent(ABC):
    """治理主体抽象接口"""
    
    @abstractmethod
    def policy(self, S: CivilizationState) -> np.ndarray:
        """返回策略向量 [低频策略, 中频策略, 高频策略]"""
        pass
    
    @abstractmethod
    def get_name(self) -> str:
        """主体名称"""
        pass
    
    @abstractmethod
    def get_type(self) -> str:
        """主体类型：AI/Human/Regulator/Enterprise"""
        pass


class AIAgent(Agent):
    """AI主体：偏向效率"""
    
    def policy(self, S: CivilizationState) -> np.ndarray:
        # 效率导向：优先提升低频（生存）和中频（协调）
        return np.array([0.6, 0.3, 0.1]) * (1 - S.survival + 0.1)
    
    def get_name(self) -> str:
        return "AI_Agent"
    
    def get_type(self) -> str:
        return "AI"


class HumanAgent(Agent):
    """人类主体：偏向意义"""
    
    def policy(self, S: CivilizationState) -> np.ndarray:
        # 意义导向：优先提升高频（意义）和中频（信任）
        return np.array([0.1, 0.3, 0.6]) * (1 - S.meaning + 0.1)
    
    def get_name(self) -> str:
        return "Human_Agent"
    
    def get_type(self) -> str:
        return "Human"


class RegulatorAgent(Agent):
    """监管主体：偏向稳定"""
    
    def policy(self, S: CivilizationState) -> np.ndarray:
        # 稳定导向：优先修复中频（信任）和低频（生存）
        return np.array([0.3, 0.5, 0.2]) * (1 - S.coordination + 0.1)
    
    def get_name(self) -> str:
        return "Regulator_Agent"
    
    def get_type(self) -> str:
        return "Regulator"


class EnterpriseAgent(Agent):
    """企业主体：偏向利益"""
    
    def policy(self, S: CivilizationState) -> np.ndarray:
        # 利益导向：均衡三频，但偏重低频
        return np.array([0.4, 0.3, 0.3]) * (1 - S.survival + 0.1)
    
    def get_name(self) -> str:
        return "Enterprise_Agent"
    
    def get_type(self) -> str:
        return "Enterprise"


class AuthorityMatrix:
    """动态权威矩阵"""
    
    def __init__(self, initial_weights: Optional[np.ndarray] = None):
        """
        初始化权威矩阵
        默认权重：[AI, Human, Regulator, Enterprise]
        """
        if initial_weights is not None:
            self.weights = initial_weights
        else:
            self.weights = np.array([0.40, 0.20, 0.25, 0.15])
        self._history: List[Dict] = []
    
    def update(self, S: CivilizationState):
        """根据系统状态更新权威矩阵"""
        if S.survival < 0.3:
            # 生存危机 → 人类权重↑
            self.weights = np.array([0.20, 0.40, 0.30, 0.10])
        elif S.coordination < 0.3:
            # 信任危机 → 监管权重↑
            self.weights = np.array([0.25, 0.25, 0.40, 0.10])
        elif S.meaning < 0.3:
            # 意义危机 → 人类权重↑
            self.weights = np.array([0.20, 0.40, 0.20, 0.20])
        else:
            # 正常状态 → 默认
            self.weights = np.array([0.40, 0.20, 0.25, 0.15])
        
        # 归一化
        self.weights = self.weights / self.weights.sum()
        
        self._history.append({
            "timestamp": time.time(),
            "weights": self.weights.copy(),
            "state": S
        })
    
    def get(self) -> np.ndarray:
        """获取当前权威矩阵"""
        return self.weights
    
    def get_history(self) -> List[Dict]:
        return self._history.copy()


def aggregate_strategies(
    agents: List[Agent],
    S: CivilizationState,
    authority: Optional[AuthorityMatrix] = None
) -> np.ndarray:
    """
    聚合多Agent策略
    
    Args:
        agents: Agent列表
        S: 当前系统状态
        authority: 权威矩阵（可选）
    
    Returns:
        聚合后的策略向量
    """
    if not agents:
        return np.zeros(3)
    
    if authority is None:
        authority = AuthorityMatrix()
    
    authority.update(S)
    strategies = np.array([a.policy(S) for a in agents])
    weights = authority.get()
    
    # 确保weights长度与策略数量匹配
    if len(weights) != len(strategies):
        weights = np.ones(len(strategies)) / len(strategies)
    
    return np.average(strategies, axis=0, weights=weights[:len(strategies)])


def create_default_agents() -> List[Agent]:
    """创建默认的四类Agent"""
    return [
        AIAgent(),
        HumanAgent(),
        RegulatorAgent(),
        EnterpriseAgent()
    ]
