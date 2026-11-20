#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec2 uvIn;
layout(location = 3) in int flags;

uniform mat4 modelMatrix;
uniform mat4 viewMatrix;
uniform mat4 projectionMatrix;
out vec2 uv;

#include vertexflagbits.ash
#include vertexwarp.vsh

void main(void)
{
    // Match standard's camera-relative transform and warping, including wheels.
    vec4 worldPos = modelMatrix * vec4(vertexPositionIn, 1.0);
    worldPos = applyVertexWarping(flags, worldPos);
    worldPos = applyGlobalWarping(worldPos);
    gl_Position = projectionMatrix * viewMatrix * worldPos;
    uv = uvIn;
}
