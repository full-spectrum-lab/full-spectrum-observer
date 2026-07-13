#!/usr/bin/env python3
"""
全频谱协议 · L0 现实锚定层
将现实世界信号映射到状态空间，施加压缩映射防止发散
"""

from abc import ABC, abstractmethod
from dataclasses import dataclass
from typing import Dict, List, Optional, Tuple
import numpy as np

from ..core.state import CivilizationState


class ObservationSource(ABC):
    """观测源接口"""
    
    @abstractmethod
    def observe(self) -> CivilizationState:
        """返回观测到的状态"""
        pass
    
    @abstractmethod
    def get_source_name(self) -> str:
        """获取观测源名称"""
        pass


@dataclass
class L0Config:
    """L0 层配置"""
    compression_alpha: float = 0.7  # 压缩映射系数 α ∈ (0,1)
    min_samples: int = 2            # 最少观测源数量


class CompositeObservationOperator:
    """
    复合观测算子：聚合多个观测源，施加压缩映射
    L₀(S) = β·S + (1-β)·S_real
    """
    
    def __init__(
        self,
        sources: List[ObservationSource],
        config: Optional[L0Config] = None,
        compression_alpha: Optional[float] = None
    ):
        if len(sources) < 2:
            raise ValueError("至少需要2个观测源")
        self.sources = sources
        self.config = config or L0Config()
        if compression_alpha is not None:
            self.config.compression_alpha = compression_alpha
        self._history: List[CivilizationState] = []
    
    def observe(self) -> CivilizationState:
        """观测现实，返回压缩后的状态"""
        # 聚合
        states = [s.observe() for s in self.sources]
        avg = CivilizationState(
            survival=np.mean([s.survival for s in states]),
            coordination=np.mean([s.coordination for s in states]),
            meaning=np.mean([s.meaning for s in states])
        )
        
        # 压缩映射：防止观测噪声导致状态突变
        if self._history:
            prev = self._history[-1]
            compressed = CivilizationState(
                survival=prev.survival + self.config.compression_alpha * (avg.survival - prev.survival),
                coordination=prev.coordination + self.config.compression_alpha * (avg.coordination - prev.coordination),
                meaning=prev.meaning + self.config.compression_alpha * (avg.meaning - prev.meaning)
            )
        else:
            compressed = avg
        
        self._history.append(compressed)
        return compressed
    
    def get_history(self) -> List[CivilizationState]:
        return self._history.copy()
    
    def reset(self):
        self._history.clear()
    
    def get_sources(self) -> List[ObservationSource]:
        return self.sources


class FixedObservationSource(ObservationSource):
    """固定值观测源（用于测试）"""
    
    def __init__(self, state: CivilizationState, name: str = "FixedSource"):
        self._state = state
        self._name = name
    
    def observe(self) -> CivilizationState:
        return self._state
    
    def get_source_name(self) -> str:
        return self._name
    
    def set_state(self, state: CivilizationState):
        self._state = state


class RandomObservationSource(ObservationSource):
    """随机观测源（用于测试）"""
    
    def __init__(self, base: CivilizationState, noise: float = 0.08, name: str = "RandomSource"):
        self.base = base
        self.noise = noise
        self._name = name
    
    def observe(self) -> CivilizationState:
        return CivilizationState(
            survival=max(0.0, min(1.0, self.base.survival + np.random.normal(0, self.noise))),
            coordination=max(0.0, min(1.0, self.base.coordination + np.random.normal(0, self.noise))),
            meaning=max(0.0, min(1.0, self.base.meaning + np.random.normal(0, self.noise)))
        )
    
    def get_source_name(self) -> str:
        return self._name


class LogBasedObservationSource(ObservationSource):
    """基于日志的观测源（企业接入模板）"""
    
    def __init__(self, name: str = "LogSource", config: Optional[Dict[str, float]] = None):
        self._name = name
        self.config = config or {}
        self._cache: Dict[str, float] = {}
    
    def observe(self) -> CivilizationState:
        return CivilizationState(
            survival=self._get_survival(),
            coordination=self._get_coordination(),
            meaning=self._get_meaning()
        )
    
    def _get_survival(self) -> float:
        """从日志计算生存层指标"""
        # TODO: 替换为真实监控数据
        return self.config.get("survival", self.config.get("low_freq", 0.85))
    
    def _get_coordination(self) -> float:
        """从日志计算协调层指标"""
        # TODO: 替换为真实跨系统日志
        return self.config.get("coordination", self.config.get("mid_freq", 0.75))
    
    def _get_meaning(self) -> float:
        """从日志计算意义层指标"""
        # TODO: 替换为真实审计数据
        return self.config.get("meaning", self.config.get("high_freq", 0.70))
    
    def get_source_name(self) -> str:
        return self._name
