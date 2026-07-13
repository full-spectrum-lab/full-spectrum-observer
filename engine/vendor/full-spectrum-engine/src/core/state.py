#!/usr/bin/env python3
"""
全频谱协议 · 核心状态模块
定义文明状态向量 S(t) = [S_l, S_m, S_h]^T
"""

from dataclasses import dataclass
from typing import Tuple
import numpy as np


@dataclass
class CivilizationState:
    """文明状态向量 S(t) = [S_l, S_m, S_h]^T"""
    survival: float      # S_l: 生存层 [0,1]
    coordination: float  # S_m: 协调层 [0,1]
    meaning: float       # S_h: 意义层 [0,1]
    
    def __post_init__(self):
        """边界约束：将所有分量限制在 [0,1] 区间"""
        self.survival = max(0.0, min(1.0, self.survival))
        self.coordination = max(0.0, min(1.0, self.coordination))
        self.meaning = max(0.0, min(1.0, self.meaning))
    
    def to_array(self) -> np.ndarray:
        """转换为 numpy 数组"""
        return np.array([self.survival, self.coordination, self.meaning], dtype=float)
    
    @classmethod
    def from_array(cls, arr: np.ndarray) -> "CivilizationState":
        """从 numpy 数组创建状态"""
        return cls(survival=arr[0], coordination=arr[1], meaning=arr[2])
    
    def is_feasible(self) -> bool:
        """
        检查是否在悲悯约束集 C 内
        C = { S ∈ [0,1]³ | S_l ≥ 0.3, S_m ≤ 0.8, S_h ≥ 0.2 }
        """
        return (self.survival >= 0.3 and 
                self.coordination <= 0.8 and 
                self.meaning >= 0.2)
    
    def distance_to(self, other: "CivilizationState") -> float:
        """计算到另一个状态的欧氏距离"""
        return float(np.linalg.norm(self.to_array() - other.to_array()))
    
    def __repr__(self) -> str:
        return f"S=({self.survival:.3f}, {self.coordination:.3f}, {self.meaning:.3f})"


def project_to_feasible(state: CivilizationState) -> CivilizationState:
    """
    觉性炸弹：将状态投影回悲悯约束集 C
    Γ(S) = argmin_{S'∈C} ||S' - S||
    """
    return CivilizationState(
        survival=max(0.3, state.survival),
        coordination=min(0.8, state.coordination),
        meaning=max(0.2, state.meaning)
    )
