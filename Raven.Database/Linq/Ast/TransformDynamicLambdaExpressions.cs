﻿using ICSharpCode.NRefactory.Ast;
using ICSharpCode.NRefactory.Visitors;

namespace Raven.Database.Linq.Ast
{
	public class TransformDynamicLambdaExpressions : AbstractAstTransformer
	{
		public override object VisitLambdaExpression(ICSharpCode.NRefactory.Ast.LambdaExpression lambdaExpression, object data)
		{
			var invocationExpression = lambdaExpression.Parent as InvocationExpression;
			if (invocationExpression == null)
				return base.VisitLambdaExpression(lambdaExpression, data);

			var target = invocationExpression.TargetObject as MemberReferenceExpression;
			if(target == null)
				return base.VisitLambdaExpression(lambdaExpression, data);

			INode node = lambdaExpression;
			var parenthesizedlambdaExpression = new ParenthesizedExpression(lambdaExpression);
			switch (target.MemberName)
			{
				case "Sum":
					node = new CastExpression(new TypeReference("Func<dynamic, decimal>"), parenthesizedlambdaExpression, CastType.Cast);
					break;
				case "OrderBy":
				case "OrderByDescending":
				case "Select":
					node = new CastExpression(new TypeReference("Func<dynamic, dynamic>"), parenthesizedlambdaExpression, CastType.Cast);
					break;
				case "SelectMany":
					node = new CastExpression(new TypeReference("Func<dynamic, IEnumerable<dynamic>>"), parenthesizedlambdaExpression, CastType.Cast);
					break;
				case "GroupBy":
					node = new CastExpression(new TypeReference("Func<dynamic, IGrouping<dynamic,dynamic>>"), parenthesizedlambdaExpression, CastType.Cast);
					break;
				case "Any":
				case "all":
				case "First":
				case "FirstOrDefault":
				case "Last":
				case "LastOfDefault":
				case "Single":
				case "Where":
				case "Count":
				case "SingleOrDefault":
					node = new CastExpression(new TypeReference("Func<dynamic, bool>"), parenthesizedlambdaExpression, CastType.Cast);
				break;
			}
			ReplaceCurrentNode(node);

			return base.VisitLambdaExpression(lambdaExpression, data);
		}
	}
}