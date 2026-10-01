"""Original-only static-initialization rejection control; no recovered acceptance."""

from array_argument_cctor import observations_for, verify_for


PROFILE = 'element-cctor-array-argument'
ASSEMBLY = 'ElementCctorArrayArgumentFixture'
KIND = 'element'
METHODS = 5
TYPES = 3


def observations():
    return observations_for(ASSEMBLY, KIND, METHODS, TYPES)


def verify(path, stage, version):
    return verify_for(path, stage, version, PROFILE, observations())
