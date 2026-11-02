def stationary(samples, max_counts=4, max_velocity=.02):
    if len(samples)<20 or samples[-1][0]-samples[0][0]<.5: return False
    positions=[s[1] for s in samples]
    return max(positions)-min(positions)<=max_counts and all(abs(s[2])<=max_velocity for s in samples)
