using System.Collections.Generic;
using MGroup.IGA.Entities;
using MGroup.MSolve.Discretization.FreedomDegrees;

namespace MGroup.IGA.Interfaces
{
	public interface ISurfaceLoadedElement
	{
		Dictionary<int, double> CalculateSurfacePressure(Element element, double pressureMagnitude);

		Dictionary<int, double> CalculateSurfaceDistributedLoad(Element element, IDofType loadedDof, double loadMagnitude);
	}
}
