"""Original-only static-initialization rejection control; no recovered acceptance."""

from array_argument_cctor import observations_for, verify_for


PROFILE = 'application-parent-cctor-array-argument'
ASSEMBLY = 'ApplicationParentCctorArrayArgumentFixture'
KIND = 'parent'
METHODS = 6
TYPES = 4


def observations():
    return observations_for(ASSEMBLY, KIND, METHODS, TYPES)


def verify(path, stage, version):
    return verify_for(path, stage, version, PROFILE, observations())
